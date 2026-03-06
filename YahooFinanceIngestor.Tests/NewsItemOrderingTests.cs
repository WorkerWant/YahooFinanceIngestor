using YahooFinanceIngestor.Common;
using YahooFinanceIngestor.Models;

namespace YahooFinanceIngestor.Tests;

[TestClass]
public sealed class NewsItemOrderingTests
{
    [TestMethod]
    public void ToStableArray_SortsByPublishedAtDescending_ThenStableKeys()
    {
        var items = new[]
        {
            CreateItem("story-b", "https://finance.yahoo.com/news/story-b.html", "Story B", "2026-03-06T11:00:00Z"),
            CreateItem("story-a", "https://finance.yahoo.com/news/story-a.html", "Story A", "2026-03-06T11:00:00Z"),
            CreateItem("story-c", "https://finance.yahoo.com/news/story-c.html", "Story C", "2026-03-06T12:00:00Z"),
            CreateItem("story-z", "https://finance.yahoo.com/news/story-z.html", "Story Z", null)
        };

        var ordered = NewsItemOrdering.ToStableArray(items);

        CollectionAssert.AreEqual(
            new[] { "story-c", "story-a", "story-b", "story-z" },
            ordered.Select(item => item.ExternalId).ToArray());
    }

    [TestMethod]
    public void ToStableArray_ReturnsSameTopN_ForDifferentInputOrders()
    {
        var canonical = new[]
        {
            CreateItem("story-2", "https://finance.yahoo.com/news/story-2.html", "Story 2", "2026-03-06T11:00:00Z"),
            CreateItem("story-4", "https://finance.yahoo.com/news/story-4.html", "Story 4", "2026-03-06T09:00:00Z"),
            CreateItem("story-1", "https://finance.yahoo.com/news/story-1.html", "Story 1", "2026-03-06T12:00:00Z"),
            CreateItem("story-3", "https://finance.yahoo.com/news/story-3.html", "Story 3", "2026-03-06T10:00:00Z")
        };

        var shuffled = new[]
        {
            canonical[1],
            canonical[3],
            canonical[0],
            canonical[2]
        };

        var firstTop = NewsItemOrdering.ToStableArray(canonical).Take(3).Select(item => item.ExternalId).ToArray();
        var secondTop = NewsItemOrdering.ToStableArray(shuffled).Take(3).Select(item => item.ExternalId).ToArray();

        CollectionAssert.AreEqual(firstTop, secondTop);
        CollectionAssert.AreEqual(new[] { "story-1", "story-2", "story-3" }, firstTop);
    }

    private static NewsItem CreateItem(string externalId, string url, string title, string? publishedAt)
    {
        return new NewsItem
        {
            ExternalId = externalId,
            Title = title,
            Url = url,
            Provider = "Yahoo Finance",
            PublishedAt = publishedAt is null ? null : DateTimeOffset.Parse(publishedAt),
            Summary = $"Summary for {externalId}",
            FetchedVia = "playwright",
            NormalizedUrl = url
        };
    }
}
