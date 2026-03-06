using Microsoft.Extensions.Logging;
using YahooFinanceIngestor.Cli;
using YahooFinanceIngestor.Common;
using YahooFinanceIngestor.Infrastructure;
using YahooFinanceIngestor.Models;
using YahooFinanceIngestor.Sources;
using System.Text.RegularExpressions;

namespace YahooFinanceIngestor.Services;

internal sealed class IngestionOrchestrator
{
    private static readonly Regex WhiteSpaceRegex = new("\\s+", RegexOptions.Compiled);
    private readonly AppOptions _options;
    private readonly INewsRepository _newsRepository;
    private readonly ApiNewsSource _apiNewsSource;
    private readonly PlaywrightNewsSource _playwrightNewsSource;
    private readonly UrlNormalizer _urlNormalizer;
    private readonly ArticleBodyFetcher _articleBodyFetcher;
    private readonly ILogger<IngestionOrchestrator> _logger;

    public IngestionOrchestrator(
        AppOptions options,
        INewsRepository newsRepository,
        ApiNewsSource apiNewsSource,
        PlaywrightNewsSource playwrightNewsSource,
        UrlNormalizer urlNormalizer,
        ArticleBodyFetcher articleBodyFetcher,
        ILogger<IngestionOrchestrator> logger)
    {
        _options = options;
        _newsRepository = newsRepository;
        _apiNewsSource = apiNewsSource;
        _playwrightNewsSource = playwrightNewsSource;
        _urlNormalizer = urlNormalizer;
        _articleBodyFetcher = articleBodyFetcher;
        _logger = logger;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Start run: source={Source}, fetchBody={FetchBody}, stopOnKnownPage={StopOnKnownPage}, playwrightApiBackfill={PlaywrightApiBackfill}, timeout={Timeout}s, maxItems={MaxItems}, dryRun={DryRun}, headless={Headless}, apiPageSize={ApiPageSize}",
            _options.Source,
            _options.FetchBody,
            _options.StopOnKnownPage,
            _options.PlaywrightApiBackfill,
            _options.TimeoutSeconds,
            _options.MaxItems?.ToString() ?? "all",
            _options.DryRun,
            _options.Headless,
            _options.ApiPageSize);

        if (_options.CheckDb)
        {
            await _newsRepository.CheckConnectionAsync(cancellationToken);
            _logger.LogInformation("Database connection check passed");
            return 0;
        }

        if (!_options.DryRun)
        {
            await _newsRepository.EnsureSchemaAsync(cancellationToken);
        }

        IReadOnlyCollection<NewsItem> raw;
        var sourceUsed = _options.Source == SourceMode.Api ? "api" : "playwright";

        if (_options.Source == SourceMode.Api)
        {
            try
            {
                raw = await _apiNewsSource.FetchAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API source failed. Fallback to Playwright started.");
                raw = await _playwrightNewsSource.FetchAsync(cancellationToken);
                sourceUsed = "playwright-fallback";
            }
        }
        else
        {
            try
            {
                raw = await _playwrightNewsSource.FetchAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Playwright source failed. Fallback to API started.");
                raw = await _apiNewsSource.FetchAsync(cancellationToken);
                sourceUsed = "api-fallback";
            }
        }

        _logger.LogInformation("Raw items fetched: {RawCount}", raw.Count);

        var prepared = Prepare(raw, sourceUsed, out var invalidCount, out var duplicateCount);
        prepared = NewsItemOrdering.ToStableArray(prepared);

        if (_options.MaxItems.HasValue && prepared.Count > _options.MaxItems.Value)
        {
            prepared = prepared.Take(_options.MaxItems.Value).ToArray();
        }

        _logger.LogInformation(
            "Prepared items: {PreparedCount} (invalid dropped={InvalidCount}, duplicates dropped={DuplicateCount})",
            prepared.Count,
            invalidCount,
            duplicateCount);

        if (_options.DryRun)
        {
            var dryRunResult = new PersistResult
            {
                Total = prepared.Count,
                Inserted = prepared.Count,
                Skipped = 0
            };

            _logger.LogInformation(
                "Completed dry-run (no DB): total={Total}, wouldInsert={WouldInsert}, skipped={Skipped}, mode={Mode}",
                dryRunResult.Total,
                dryRunResult.Inserted,
                dryRunResult.Skipped,
                sourceUsed);

            return 0;
        }

        var persistResult = await _newsRepository.PersistNewsAsync(prepared, cancellationToken);

        var fetchedBodies = 0;
        if (_options.FetchBody && prepared.Count > 0)
        {
            var rowsMissingBody = await _newsRepository.GetRowsMissingArticleBodyAsync(prepared, cancellationToken);
            if (rowsMissingBody.Count > 0)
            {
                fetchedBodies = await _articleBodyFetcher.BackfillBodiesAsync(rowsMissingBody, cancellationToken);
            }
        }

        _logger.LogInformation(
            "Completed run: total={Total}, inserted={Inserted}, skipped={Skipped}, bodiesLoaded={BodiesLoaded}, mode={Mode}",
            persistResult.Total,
            persistResult.Inserted,
            persistResult.Skipped,
            fetchedBodies,
            sourceUsed);

        return 0;
    }

    private IReadOnlyCollection<NewsItem> Prepare(
        IReadOnlyCollection<NewsItem> raw,
        string sourceUsed,
        out int invalidCount,
        out int duplicateCount)
    {
        var prepared = new List<NewsItem>(raw.Count);
        var seenExternalIds = new HashSet<string>(StringComparer.Ordinal);
        var seenUrls = new HashSet<string>(StringComparer.Ordinal);

        invalidCount = 0;
        duplicateCount = 0;

        foreach (var item in raw)
        {
            var title = NormalizeWhitespace(item.Title);
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(item.Url))
            {
                invalidCount++;
                continue;
            }

            var normalizedUrl = _urlNormalizer.Normalize(item.Url);
            if (string.IsNullOrWhiteSpace(normalizedUrl))
            {
                invalidCount++;
                continue;
            }

            var externalId = string.IsNullOrWhiteSpace(item.ExternalId) ? null : item.ExternalId.Trim();
            var hasExternalDuplicate = externalId is not null && !seenExternalIds.Add(externalId);
            var hasUrlDuplicate = !seenUrls.Add(normalizedUrl);

            if (hasExternalDuplicate || hasUrlDuplicate)
            {
                duplicateCount++;
                continue;
            }

            prepared.Add(new NewsItem
            {
                ExternalId = externalId,
                Title = title,
                Url = item.Url,
                Provider = item.Provider,
                PublishedAt = item.PublishedAt,
                Summary = item.Summary,
                FetchedVia = sourceUsed,
                NormalizedUrl = normalizedUrl
            });
        }

        return prepared;
    }

    private static string NormalizeWhitespace(string value)
    {
        return WhiteSpaceRegex.Replace(value, " ").Trim();
    }
}
