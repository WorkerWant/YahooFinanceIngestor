using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using YahooFinanceIngestor.Models;

namespace YahooFinanceIngestor.Parsing;

internal static class YahooParser
{
    private static readonly Regex EmbeddedTopicsFeedRegex = new(
        """<script type=\"application/json\" data-sveltekit-fetched[^>]*data-url=\"([^\"]*topicsDetailFeed[^\"]*)\"[^>]*>(.*?)</script>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex SpaceBeforePunctuationRegex = new(@"\s+([.,;:!?])", RegexOptions.Compiled);

    public static string ExtractLatestNewsListId(string json)
    {
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("cdsData", out var cdsData) ||
            !cdsData.TryGetProperty("topics", out var topics))
        {
            throw new InvalidOperationException("Unable to parse editorial topics payload.");
        }

        foreach (var topic in topics.EnumerateArray())
        {
            var topicName = topic.TryGetProperty("topicName", out var topicNameElement)
                ? topicNameElement.GetString()
                : null;

            if (!string.Equals(topicName, "latest-news", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var listId = topic.TryGetProperty("listId", out var listIdElement)
                ? listIdElement.GetString()
                : null;

            if (!string.IsNullOrWhiteSpace(listId))
            {
                return listId;
            }
        }

        throw new InvalidOperationException("topicName=latest-news not found in editorial topics payload.");
    }

    public static NcpPageResult ParseNcpPage(string json, string fetchedVia)
    {
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("main", out var main))
        {
            throw new InvalidOperationException("Unable to parse NCP payload.");
        }

        var items = new List<NewsItem>();

        if (main.TryGetProperty("stream", out var stream))
        {
            foreach (var streamItem in stream.EnumerateArray())
            {
                if (!streamItem.TryGetProperty("content", out var content))
                {
                    continue;
                }

                var item = ParseContentItem(content, fetchedVia);
                if (item is not null)
                {
                    items.Add(item);
                }
            }
        }

        var nextPage = main.TryGetProperty("nextPage", out var nextPageElement) && nextPageElement.GetBoolean();

        string? paginationToken = null;
        if (main.TryGetProperty("pagination", out var pagination) &&
            pagination.TryGetProperty("uuids", out var uuidsElement) &&
            uuidsElement.ValueKind == JsonValueKind.String)
        {
            paginationToken = uuidsElement.GetString();
        }

        return new NcpPageResult
        {
            Items = items,
            NextPage = nextPage,
            PaginationToken = paginationToken
        };
    }

    public static IReadOnlyCollection<NewsItem> ExtractInitialFeedFromHtml(string html, string fetchedVia)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return Array.Empty<NewsItem>();
        }

        var match = EmbeddedTopicsFeedRegex.Match(html);
        if (!match.Success)
        {
            return Array.Empty<NewsItem>();
        }

        var scriptBody = WebUtility.HtmlDecode(match.Groups[2].Value);
        using var outer = JsonDocument.Parse(scriptBody);

        if (!outer.RootElement.TryGetProperty("body", out var bodyElement) || bodyElement.ValueKind != JsonValueKind.String)
        {
            return Array.Empty<NewsItem>();
        }

        var bodyPayload = bodyElement.GetString();
        if (string.IsNullOrWhiteSpace(bodyPayload))
        {
            return Array.Empty<NewsItem>();
        }

        return ParseNcpPage(bodyPayload, fetchedVia).Items;
    }

    public static string? ExtractArticleBodyFromYahooNewsPayload(string json)
    {
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array ||
            items.GetArrayLength() == 0)
        {
            return null;
        }

        var first = items[0];
        if (!first.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("contentMeta", out var contentMeta))
        {
            return null;
        }

        var chunks = new List<string>();
        if (contentMeta.TryGetProperty("storyAtoms", out var storyAtoms) &&
            storyAtoms.ValueKind == JsonValueKind.Array)
        {
            foreach (var atom in storyAtoms.EnumerateArray())
            {
                if (!atom.TryGetProperty("content", out var contentElement) ||
                    contentElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var normalized = NormalizeHtmlText(contentElement.GetString());
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    chunks.Add(normalized);
                }
            }
        }

        if (chunks.Count > 0)
        {
            return string.Join("\n", chunks);
        }

        if (contentMeta.TryGetProperty("contentSummaries", out var contentSummaries) &&
            contentSummaries.ValueKind == JsonValueKind.Object &&
            contentSummaries.TryGetProperty("summary", out var summaryElement) &&
            summaryElement.ValueKind == JsonValueKind.String)
        {
            return NormalizeHtmlText(summaryElement.GetString());
        }

        return null;
    }

    private static NewsItem? ParseContentItem(JsonElement content, string fetchedVia)
    {
        var title = GetString(content, "title");
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var url = ExtractUrl(content);
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        DateTimeOffset? publishedAt = null;
        var publishedAtRaw = GetString(content, "pubDate");
        if (!string.IsNullOrWhiteSpace(publishedAtRaw) && DateTimeOffset.TryParse(publishedAtRaw, out var parsedPublishedAt))
        {
            publishedAt = parsedPublishedAt;
        }

        string? provider = null;
        if (content.TryGetProperty("provider", out var providerElement) &&
            providerElement.ValueKind == JsonValueKind.Object &&
            providerElement.TryGetProperty("displayName", out var displayNameElement) &&
            displayNameElement.ValueKind == JsonValueKind.String)
        {
            provider = displayNameElement.GetString();
        }

        return new NewsItem
        {
            ExternalId = GetString(content, "id"),
            Title = CompressWhitespace(title),
            Url = url,
            Provider = provider,
            PublishedAt = publishedAt,
            Summary = GetString(content, "summary"),
            FetchedVia = fetchedVia
        };
    }

    private static string? ExtractUrl(JsonElement content)
    {
        if (content.TryGetProperty("canonicalUrl", out var canonicalUrl))
        {
            if (canonicalUrl.ValueKind == JsonValueKind.Object &&
                canonicalUrl.TryGetProperty("url", out var canonicalUrlValue) &&
                canonicalUrlValue.ValueKind == JsonValueKind.String)
            {
                return canonicalUrlValue.GetString();
            }

            if (canonicalUrl.ValueKind == JsonValueKind.String)
            {
                return canonicalUrl.GetString();
            }
        }

        if (content.TryGetProperty("providerContentUrl", out var providerContentUrl))
        {
            if (providerContentUrl.ValueKind == JsonValueKind.String)
            {
                return providerContentUrl.GetString();
            }

            if (providerContentUrl.ValueKind == JsonValueKind.Object &&
                providerContentUrl.TryGetProperty("url", out var providerUrlValue) &&
                providerUrlValue.ValueKind == JsonValueKind.String)
            {
                return providerUrlValue.GetString();
            }
        }

        if (content.TryGetProperty("previewUrl", out var previewUrl) && previewUrl.ValueKind == JsonValueKind.String)
        {
            return previewUrl.GetString();
        }

        return null;
    }

    private static string? GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string CompressWhitespace(string value)
    {
        return Regex.Replace(value, @"\s+", " ").Trim();
    }

    private static string NormalizeHtmlText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decoded = WebUtility.HtmlDecode(value);
        var withoutTags = HtmlTagRegex.Replace(decoded, " ");
        var withoutExtraPunctuationSpaces = SpaceBeforePunctuationRegex.Replace(withoutTags, "$1");
        return CompressWhitespace(withoutExtraPunctuationSpaces);
    }
}
