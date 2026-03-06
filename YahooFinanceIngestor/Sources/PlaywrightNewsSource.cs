using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using YahooFinanceIngestor.Cli;
using YahooFinanceIngestor.Common;
using YahooFinanceIngestor.Infrastructure;
using YahooFinanceIngestor.Models;
using YahooFinanceIngestor.Parsing;

namespace YahooFinanceIngestor.Sources;

internal sealed class PlaywrightNewsSource : INewsSource
{
    private static readonly string[] ChromiumArgs =
    [
        "--disable-background-networking",
        "--disable-background-timer-throttling",
        "--disable-backgrounding-occluded-windows",
        "--disable-component-update",
        "--disable-default-apps",
        "--disable-extensions",
        "--disable-renderer-backgrounding",
        "--mute-audio",
        "--no-first-run"
    ];

    private readonly AppOptions _options;
    private readonly ApiNewsSource _apiNewsSource;
    private readonly INewsRepository _newsRepository;
    private readonly UrlNormalizer _urlNormalizer;
    private readonly ILogger<PlaywrightNewsSource> _logger;

    public PlaywrightNewsSource(
        AppOptions options,
        ApiNewsSource apiNewsSource,
        INewsRepository newsRepository,
        UrlNormalizer urlNormalizer,
        ILogger<PlaywrightNewsSource> logger)
    {
        _options = options;
        _apiNewsSource = apiNewsSource;
        _newsRepository = newsRepository;
        _urlNormalizer = urlNormalizer;
        _logger = logger;
    }

    public string Name => "playwright";

    public async Task<IReadOnlyCollection<NewsItem>> FetchAsync(CancellationToken cancellationToken)
    {
        var items = new ConcurrentDictionary<string, NewsItem>(StringComparer.Ordinal);
        var seenKeys = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var stopOnKnownPage = 0;

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = _options.Headless,
            Args = ChromiumArgs
        });

        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = YahooConstants.UserAgent,
            Locale = "en-US"
        });

        context.SetDefaultTimeout(Math.Max(10_000, _options.TimeoutSeconds * 1000));

        await context.RouteAsync("**/*", route =>
        {
            var resourceType = route.Request.ResourceType;
            var requestUrl = route.Request.Url.ToLowerInvariant();

            if (resourceType is "stylesheet" or "image" or "font" or "media")
            {
                return route.AbortAsync();
            }

            if (YahooConstants.BlockedDomains.Any(blocked => requestUrl.Contains(blocked, StringComparison.OrdinalIgnoreCase)))
            {
                return route.AbortAsync();
            }

            return route.ContinueAsync();
        });

        var page = await context.NewPageAsync();

        page.Response += async (_, response) =>
        {
            try
            {
                if (response.Status != 200 ||
                    !response.Url.Contains("/xhr/ncp", StringComparison.OrdinalIgnoreCase) ||
                    !response.Url.Contains("queryRef=topicsDetailFeed", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var payload = await response.TextAsync();
                var parsed = YahooParser.ParseNcpPage(payload, fetchedVia: "playwright");

                NormalizeForLookup(parsed.Items);
                if (await IsPageFullyKnownAsync(parsed.Items, cancellationToken))
                {
                    if (_options.FetchBody)
                    {
                        AddUniqueItems(parsed.Items, items, seenKeys, respectMaxItems: true);
                    }

                    Interlocked.Exchange(ref stopOnKnownPage, 1);
                    return;
                }

                AddUniqueItems(parsed.Items, items, seenKeys, respectMaxItems: true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to parse Playwright-captured NCP response");
            }
        };

        await page.GotoAsync(YahooConstants.TopicPageUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = Math.Max(10_000, _options.TimeoutSeconds * 1000)
        });

        await DismissConsentIfVisibleAsync(page);
        await page.WaitForTimeoutAsync(800);

        var initialHtml = await page.ContentAsync();
        var initialItems = YahooParser.ExtractInitialFeedFromHtml(initialHtml, fetchedVia: "playwright");
        NormalizeForLookup(initialItems);
        if (!await IsPageFullyKnownAsync(initialItems, cancellationToken))
        {
            AddUniqueItems(initialItems, items, seenKeys, respectMaxItems: true);
        }
        else
        {
            if (_options.FetchBody)
            {
                AddUniqueItems(initialItems, items, seenKeys, respectMaxItems: true);
            }

            Interlocked.Exchange(ref stopOnKnownPage, 1);
        }

        await ScrollToBottomAsync(page, items, () => Volatile.Read(ref stopOnKnownPage) == 1, cancellationToken);
        await page.WaitForTimeoutAsync(1000);

        if (!_options.PlaywrightApiBackfill)
        {
            _logger.LogInformation(
                "Playwright API backfill disabled. Returning browser-collected items only.");
        }
        else
        {
            try
            {
                if (_options.MaxItems.HasValue && items.Count >= _options.MaxItems.Value)
                {
                    _logger.LogInformation(
                        "Playwright source reached max-items={MaxItems} in browser mode. Running API backfill anyway to stabilize canonical top-N ordering.",
                        _options.MaxItems.Value);
                }

                var apiItems = await _apiNewsSource.FetchAsync(cancellationToken);
                AddUniqueItems(apiItems, items, seenKeys, respectMaxItems: false);
                _logger.LogInformation("Playwright source backfilled {Count} items via optimized API path", apiItems.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Optimized API backfill failed inside Playwright source. Continuing with browser-collected data only.");
            }
        }

        var result = NewsItemOrdering.ToStableArray(items.Values);
        _logger.LogInformation("Playwright source collected {Count} raw items", result.Length);
        return result;
    }

    private async Task ScrollToBottomAsync(
        IPage page,
        ConcurrentDictionary<string, NewsItem> items,
        Func<bool> shouldStopEarly,
        CancellationToken cancellationToken)
    {
        var stableIterations = 0;
        var previousHeight = 0.0;
        var previousCount = items.Count;

        for (var i = 0; i < 600; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (shouldStopEarly())
            {
                _logger.LogInformation("Playwright scroll stopped after encountering a fully known page in DB.");
                break;
            }

            await page.EvaluateAsync("() => window.scrollTo(0, document.body.scrollHeight)");
            await page.WaitForTimeoutAsync(700);

            var nextHeight = await page.EvaluateAsync<double>("() => document.body.scrollHeight");
            var nextCount = items.Count;

            if (Math.Abs(nextHeight - previousHeight) < 0.5 && nextCount == previousCount)
            {
                stableIterations++;
            }
            else
            {
                stableIterations = 0;
            }

            previousHeight = nextHeight;
            previousCount = nextCount;

            if (_options.MaxItems.HasValue && nextCount >= _options.MaxItems.Value)
            {
                _logger.LogInformation("Playwright scroll stopped at max-items={MaxItems}", _options.MaxItems.Value);
                break;
            }

            if (stableIterations >= 8)
            {
                _logger.LogInformation("Playwright scroll reached stable end after {Iterations} iterations", i + 1);
                break;
            }
        }
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

    private void AddUniqueItems(
        IReadOnlyCollection<NewsItem> items,
        ConcurrentDictionary<string, NewsItem> collector,
        ConcurrentDictionary<string, byte> seenKeys,
        bool respectMaxItems)
    {
        foreach (var item in items)
        {
            if (respectMaxItems && _options.MaxItems.HasValue && collector.Count >= _options.MaxItems.Value)
            {
                break;
            }

            var key = BuildItemKey(item);
            if (seenKeys.TryAdd(key, 0))
            {
                collector[key] = item;
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

    private static string BuildItemKey(NewsItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.NormalizedUrl))
        {
            return $"url:{item.NormalizedUrl}";
        }

        if (!string.IsNullOrWhiteSpace(item.ExternalId))
        {
            return $"id:{item.ExternalId.Trim()}";
        }

        return $"raw:{item.Url}";
    }

    private static async Task DismissConsentIfVisibleAsync(IPage page)
    {
        var selectors = new[]
        {
            "button:has-text('Accept all')",
            "button:has-text('I agree')",
            "button:has-text('Alle akzeptieren')",
            "button[name='agree']"
        };

        foreach (var selector in selectors)
        {
            try
            {
                var locator = page.Locator(selector).First;
                if (await locator.IsVisibleAsync())
                {
                    await locator.ClickAsync();
                    await page.WaitForTimeoutAsync(500);
                    return;
                }
            }
            catch
            {
                // Ignore and continue trying alternate selectors.
            }
        }
    }
}
