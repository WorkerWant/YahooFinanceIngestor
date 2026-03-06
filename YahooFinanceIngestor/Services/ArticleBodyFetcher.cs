using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using YahooFinanceIngestor.Cli;
using YahooFinanceIngestor.Common;
using YahooFinanceIngestor.Infrastructure;
using YahooFinanceIngestor.Models;
using YahooFinanceIngestor.Parsing;

namespace YahooFinanceIngestor.Services;

internal sealed class ArticleBodyFetcher
{
    private static readonly Regex JsonLdScriptRegex = new(
        """<script[^>]*type=[\"']application/ld\+json[\"'][^>]*>(.*?)</script>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ParagraphRegex = new(
        """<p[^>]*>(.*?)</p>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex MetaDescriptionRegex = new(
        """<meta\s+name=[\"']description[\"']\s+content=[\"'](.*?)[\"']\s*/?>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhiteSpaceRegex = new("\\s+", RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly INewsRepository _newsRepository;
    private readonly AppOptions _options;
    private readonly ILogger<ArticleBodyFetcher> _logger;

    public ArticleBodyFetcher(
        HttpClient httpClient,
        INewsRepository newsRepository,
        AppOptions options,
        ILogger<ArticleBodyFetcher> logger)
    {
        _httpClient = httpClient;
        _newsRepository = newsRepository;
        _options = options;
        _logger = logger;
    }

    public async Task<int> BackfillBodiesAsync(IReadOnlyCollection<StoredNews> newsRows, CancellationToken cancellationToken)
    {
        if (!newsRows.Any())
        {
            return 0;
        }

        var success = 0;
        using var semaphore = new SemaphoreSlim(Math.Max(1, _options.BodyConcurrency));

        var tasks = newsRows.Select(async item =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var body = await TryLoadBodyAsync(item.Url, cancellationToken);
                if (string.IsNullOrWhiteSpace(body))
                {
                    return;
                }

                await _newsRepository.UpdateArticleBodyAsync(item.Id, body, cancellationToken);
                Interlocked.Increment(ref success);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Body fetch failed for url={Url}", item.Url);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return success;
    }

    private async Task<string?> TryLoadBodyAsync(string url, CancellationToken cancellationToken)
    {
        var yahooNewsBody = await TryLoadBodyFromYahooNewsApiAsync(url, cancellationToken);
        if (!string.IsNullOrWhiteSpace(yahooNewsBody))
        {
            return yahooNewsBody;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("accept", "text/html,application/xhtml+xml");
        request.Headers.UserAgent.ParseAdd(YahooConstants.UserAgent);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var body = ExtractFromJsonLd(html) ?? ExtractFromParagraphs(html);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var compact = WhiteSpaceRegex.Replace(body, " ").Trim();
        return compact.Length > 40_000 ? compact[..40_000] : compact;
    }

    private async Task<string?> TryLoadBodyFromYahooNewsApiAsync(string url, CancellationToken cancellationToken)
    {
        var requestUri = BuildYahooNewsRequestUri(url);
        if (requestUri is null)
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.TryAddWithoutValidation("accept", "application/json");
        request.Headers.TryAddWithoutValidation("x-requested-with", "XMLHttpRequest");
        request.Headers.UserAgent.ParseAdd(YahooConstants.UserAgent);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        var body = YahooParser.ExtractArticleBodyFromYahooNewsPayload(payload);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        return body.Length > 40_000 ? body[..40_000] : body;
    }

    private static Uri? BuildYahooNewsRequestUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Host, "finance.yahoo.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith("/news/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = uri.AbsolutePath.TrimStart('/');
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var builder = new UriBuilder(YahooConstants.NewsDetailsUrl)
        {
            Query =
                $"appid=finance_web&features={Uri.EscapeDataString(YahooConstants.NewsDetailsFeatures)}&site=finance&lang=en-US&region=US&url={Uri.EscapeDataString(path)}"
        };

        return builder.Uri;
    }

    private static string? ExtractFromJsonLd(string html)
    {
        var matches = JsonLdScriptRegex.Matches(html);
        foreach (Match match in matches)
        {
            var payload = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
            if (string.IsNullOrWhiteSpace(payload))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(payload);
                var value = FindStringProperty(doc.RootElement, "articleBody")
                            ?? FindStringProperty(doc.RootElement, "description");

                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
            catch
            {
                // ignore malformed JSON-LD blocks
            }
        }

        return null;
    }

    private static string? FindStringProperty(JsonElement element, string propertyName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals(propertyName) && property.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = property.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            return value;
                        }
                    }

                    var nested = FindStringProperty(property.Value, propertyName);
                    if (!string.IsNullOrWhiteSpace(nested))
                    {
                        return nested;
                    }
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = FindStringProperty(item, propertyName);
                    if (!string.IsNullOrWhiteSpace(nested))
                    {
                        return nested;
                    }
                }
                break;
        }

        return null;
    }

    private static string? ExtractFromParagraphs(string html)
    {
        var chunks = new List<string>();

        foreach (Match match in ParagraphRegex.Matches(html))
        {
            var text = HtmlTagRegex.Replace(System.Net.WebUtility.HtmlDecode(match.Groups[1].Value), " ").Trim();
            text = WhiteSpaceRegex.Replace(text, " ").Trim();

            if (text.Length >= 40)
            {
                chunks.Add(text);
            }

            if (chunks.Count >= 25)
            {
                break;
            }
        }

        if (chunks.Count > 0)
        {
            return string.Join("\n", chunks);
        }

        var meta = MetaDescriptionRegex.Match(html);
        if (meta.Success)
        {
            return WhiteSpaceRegex.Replace(System.Net.WebUtility.HtmlDecode(meta.Groups[1].Value), " ").Trim();
        }

        return null;
    }
}
