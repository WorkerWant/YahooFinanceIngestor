using YahooFinanceIngestor.Models;

namespace YahooFinanceIngestor.Common;

internal static class NewsItemOrdering
{
    public static readonly IComparer<NewsItem> StableComparer = Comparer<NewsItem>.Create(Compare);

    public static NewsItem[] ToStableArray(IEnumerable<NewsItem> items)
    {
        var list = items.ToList();
        list.Sort(StableComparer);
        return list.ToArray();
    }

    private static int Compare(NewsItem? left, NewsItem? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return 1;
        }

        if (right is null)
        {
            return -1;
        }

        var publishedAt = Comparer<DateTimeOffset?>.Default.Compare(right.PublishedAt, left.PublishedAt);
        if (publishedAt != 0)
        {
            return publishedAt;
        }

        var externalId = StringComparer.Ordinal.Compare(Normalize(left.ExternalId), Normalize(right.ExternalId));
        if (externalId != 0)
        {
            return externalId;
        }

        var normalizedUrl = StringComparer.Ordinal.Compare(GetStableUrlKey(left), GetStableUrlKey(right));
        if (normalizedUrl != 0)
        {
            return normalizedUrl;
        }

        var title = StringComparer.Ordinal.Compare(Normalize(left.Title), Normalize(right.Title));
        if (title != 0)
        {
            return title;
        }

        var provider = StringComparer.Ordinal.Compare(Normalize(left.Provider), Normalize(right.Provider));
        if (provider != 0)
        {
            return provider;
        }

        return StringComparer.Ordinal.Compare(Normalize(left.Summary), Normalize(right.Summary));
    }

    private static string GetStableUrlKey(NewsItem item)
    {
        return Normalize(string.IsNullOrWhiteSpace(item.NormalizedUrl) ? item.Url : item.NormalizedUrl);
    }

    private static string Normalize(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }
}
