using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using YahooFinanceIngestor.Cli;
using YahooFinanceIngestor.Common;
using YahooFinanceIngestor.Infrastructure;
using YahooFinanceIngestor.Models;
using YahooFinanceIngestor.Parsing;

namespace YahooFinanceIngestor.Sources;

internal sealed class ApiNewsSource : INewsSource
{
    private readonly HttpClient _httpClient;
    private readonly AppOptions _options;
    private readonly INewsRepository _newsRepository;
    private readonly UrlNormalizer _urlNormalizer;
    private readonly ILogger<ApiNewsSource> _logger;

    public ApiNewsSource(
        HttpClient httpClient,
        AppOptions options,
        INewsRepository newsRepository,
        UrlNormalizer urlNormalizer,
        ILogger<ApiNewsSource> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _newsRepository = newsRepository;
        _urlNormalizer = urlNormalizer;
        _logger = logger;
    }

    public string Name => "api";

    public async Task<IReadOnlyCollection<NewsItem>> FetchAsync(CancellationToken cancellationToken)
    {
        var listId = await GetListIdAsync(cancellationToken);
        _logger.LogInformation("API source resolved latest-news listId: {ListId}", listId);

        var items = new List<NewsItem>();
        string? paginationToken = null;

        for (var page = 1; page <= 200; page++)
        {
            var pageSize = ResolveRequestPageSize(items.Count);
            var result = await FetchPageAsync(listId, paginationToken, pageSize, cancellationToken);
            NormalizeForLookup(result.Items);

            var pageFullyKnown = await IsPageFullyKnownAsync(result.Items, cancellationToken);
            if (pageFullyKnown)
            {
                if (_options.FetchBody)
                {
                    items.AddRange(result.Items);
                    _logger.LogInformation(
                        "API page {Page} is fully known in DB. Collected {ItemsCount} items for body backfill and stopped (total={Total}).",
                        page,
                        result.Items.Count,
                        items.Count);
                }
                else
                {
                    _logger.LogInformation(
                        "API page {Page} is fully known in DB. Incremental fetch stopped after {Total} collected items.",
                        page,
                        items.Count);
                }
                break;
            }

            items.AddRange(result.Items);

            _logger.LogInformation(
                "API page {Page}: got {ItemsCount} items (total={Total})",
                page,
                result.Items.Count,
                items.Count);

            if (_options.MaxItems.HasValue && items.Count >= _options.MaxItems.Value)
            {
                return items.Take(_options.MaxItems.Value).ToArray();
            }

            if (!result.NextPage || string.IsNullOrWhiteSpace(result.PaginationToken))
            {
                break;
            }

            paginationToken = result.PaginationToken;
        }

        return _options.MaxItems.HasValue && items.Count > _options.MaxItems.Value
            ? items.Take(_options.MaxItems.Value).ToArray()
            : items;
    }

    private async Task<string> GetListIdAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, YahooConstants.EditorialTopicsUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("x-requested-with", "XMLHttpRequest");
        request.Headers.UserAgent.ParseAdd(YahooConstants.UserAgent);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        return YahooParser.ExtractLatestNewsListId(payload);
    }

    private async Task<NcpPageResult> FetchPageAsync(
        string listId,
        string? paginationToken,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var requestBody = BuildNcpRequestBody(listId, paginationToken, pageSize);

        using var request = new HttpRequestMessage(HttpMethod.Post, YahooConstants.TopicsDetailFeedUrl)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("origin", "https://finance.yahoo.com");
        request.Headers.Referrer = new Uri(YahooConstants.TopicPageUrl);
        request.Headers.TryAddWithoutValidation("x-requested-with", "XMLHttpRequest");
        request.Headers.UserAgent.ParseAdd(YahooConstants.UserAgent);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        return YahooParser.ParseNcpPage(payload, fetchedVia: "api");
    }

    private string BuildNcpRequestBody(string listId, string? paginationToken, int pageSize)
    {
        object main;
        if (string.IsNullOrWhiteSpace(paginationToken))
        {
            main = new { };
        }
        else
        {
            main = new
            {
                pagination = new
                {
                    uuids = paginationToken
                }
            };
        }

        var body = new
        {
            payload = new
            {
                gqlVariables = new
                {
                    main
                }
            },
            serviceConfig = new
            {
                listId,
                snippetCount = pageSize,
                count = pageSize
            }
        };

        return JsonSerializer.Serialize(body);
    }

    private int ResolveRequestPageSize(int alreadyCollected)
    {
        var pageSize = _options.ApiPageSize;

        if (_options.MaxItems.HasValue)
        {
            var remaining = _options.MaxItems.Value - alreadyCollected;
            if (remaining > 0)
            {
                pageSize = Math.Min(pageSize, remaining);
            }
        }

        return Math.Clamp(pageSize, 1, 250);
    }

    private void NormalizeForLookup(IReadOnlyCollection<NewsItem> items)
    {
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.NormalizedUrl))
            {
                item.NormalizedUrl = _urlNormalizer.Normalize(item.Url);
            }
        }
    }

    private async Task<bool> IsPageFullyKnownAsync(IReadOnlyCollection<NewsItem> items, CancellationToken cancellationToken)
    {
        if (_options.DryRun || !_options.StopOnKnownPage || items.Count == 0)
        {
            return false;
        }

        return await _newsRepository.AreAllKnownAsync(items, cancellationToken);
    }
}
