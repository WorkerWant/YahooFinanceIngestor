using System.Net;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YahooFinanceIngestor.Parsing;

namespace YahooFinanceIngestor.Tests;

[TestClass]
public sealed class YahooParserTests
{
    [TestMethod]
    public void ExtractLatestNewsListId_ReturnsLatestNewsListId()
    {
        const string json = """
            {
              "data": {
                "cdsData": {
                  "topics": [
                    { "topicName": "stock-market-news", "listId": "list-other" },
                    { "topicName": "latest-news", "listId": "list-latest" }
                  ]
                }
              }
            }
            """;

        var listId = YahooParser.ExtractLatestNewsListId(json);

        Assert.AreEqual("list-latest", listId);
    }

    [TestMethod]
    public void ParseNcpPage_ParsesTitlesUrlsAndPagination()
    {
        const string json = """
            {
              "data": {
                "main": {
                  "stream": [
                    {
                      "content": {
                        "id": "story-1",
                        "title": "  First   title  ",
                        "summary": "Summary",
                        "pubDate": "2026-03-06T08:00:00Z",
                        "provider": { "displayName": "Yahoo Finance" },
                        "canonicalUrl": { "url": "https://finance.yahoo.com/news/story-1.html" }
                      }
                    }
                  ],
                  "nextPage": true,
                  "pagination": {
                    "uuids": "pagination-token"
                  }
                }
              }
            }
            """;

        var page = YahooParser.ParseNcpPage(json, fetchedVia: "api");

        Assert.HasCount(1, page.Items);
        Assert.IsTrue(page.NextPage);
        Assert.AreEqual("pagination-token", page.PaginationToken);

        var item = page.Items.Single();
        Assert.AreEqual("story-1", item.ExternalId);
        Assert.AreEqual("First title", item.Title);
        Assert.AreEqual("https://finance.yahoo.com/news/story-1.html", item.Url);
        Assert.AreEqual("Yahoo Finance", item.Provider);
        Assert.AreEqual("api", item.FetchedVia);
        Assert.AreEqual(DateTimeOffset.Parse("2026-03-06T08:00:00Z"), item.PublishedAt);
    }

    [TestMethod]
    public void ExtractInitialFeedFromHtml_ReadsEmbeddedSveltePayload()
    {
        const string ncpJson = """
            {
              "data": {
                "main": {
                  "stream": [
                    {
                      "content": {
                        "id": "story-embedded",
                        "title": "Embedded title",
                        "canonicalUrl": { "url": "https://example.com/embedded-story" }
                      }
                    }
                  ],
                  "nextPage": false,
                  "pagination": {
                    "uuids": ""
                  }
                }
              }
            }
            """;

        var outer = JsonSerializer.Serialize(new
        {
            status = 200,
            statusText = "OK",
            headers = new Dictionary<string, string> { ["content-type"] = "application/json" },
            body = ncpJson
        });

        var html = $"""
            <html>
              <body>
                <script type="application/json" data-sveltekit-fetched data-url="/xhr/ncp?queryRef=topicsDetailFeed">{WebUtility.HtmlEncode(outer)}</script>
              </body>
            </html>
            """;

        var items = YahooParser.ExtractInitialFeedFromHtml(html, fetchedVia: "playwright");

        Assert.HasCount(1, items);
        Assert.AreEqual("story-embedded", items.Single().ExternalId);
        Assert.AreEqual("Embedded title", items.Single().Title);
        Assert.AreEqual("https://example.com/embedded-story", items.Single().Url);
    }

    [TestMethod]
    public void ExtractArticleBodyFromYahooNewsPayload_ReadsStoryAtoms()
    {
        const string json = """
            {
              "items": [
                {
                  "data": {
                    "contentMeta": {
                      "contentSummaries": {
                        "summary": "Fallback summary"
                      },
                      "storyAtoms": [
                        {
                          "type": "IMAGE"
                        },
                        {
                          "type": "TEXT",
                          "content": " First paragraph "
                        },
                        {
                          "type": "TEXT",
                          "content": "Second with <a href=\"https://example.com\">link</a>."
                        }
                      ]
                    }
                  }
                }
              ]
            }
            """;

        var body = YahooParser.ExtractArticleBodyFromYahooNewsPayload(json);

        Assert.AreEqual("First paragraph\nSecond with link.", body);
    }
}
