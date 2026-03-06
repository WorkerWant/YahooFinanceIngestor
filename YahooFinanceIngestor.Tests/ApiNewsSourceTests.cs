using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using YahooFinanceIngestor.Cli;
using YahooFinanceIngestor.Infrastructure;
using YahooFinanceIngestor.Models;
using YahooFinanceIngestor.Sources;

namespace YahooFinanceIngestor.Tests;

[TestClass]
public sealed class ApiNewsSourceTests
{
    [TestMethod]
    public async Task FetchAsync_ReturnsNoItems_WhenFirstPageIsKnown_AndFetchBodyIsDisabled()
    {
        var source = CreateSource(fetchBody: false, areAllKnown: true);

        var items = await source.FetchAsync(CancellationToken.None);

        Assert.HasCount(0, items);
    }

    [TestMethod]
    public async Task FetchAsync_ReturnsKnownPage_WhenFetchBodyIsEnabled()
    {
        var source = CreateSource(fetchBody: true, areAllKnown: true);

        var items = await source.FetchAsync(CancellationToken.None);

        Assert.HasCount(1, items);
        Assert.AreEqual("story-1", items.Single().ExternalId);
        Assert.AreEqual("https://finance.yahoo.com/news/story-1.html", items.Single().Url);
    }

    private static ApiNewsSource CreateSource(bool fetchBody, bool areAllKnown)
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/xhr/cds")
            {
                const string editorialTopicsJson = """
                    {
                      "data": {
                        "cdsData": {
                          "topics": [
                            { "topicName": "latest-news", "listId": "list-latest" }
                          ]
                        }
                      }
                    }
                    """;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(editorialTopicsJson)
                };
            }

            if (request.RequestUri?.AbsolutePath == "/xhr/ncp")
            {
                const string ncpJson = """
                    {
                      "data": {
                        "main": {
                          "stream": [
                            {
                              "content": {
                                "id": "story-1",
                                "title": "Story 1",
                                "provider": { "displayName": "Yahoo Finance" },
                                "canonicalUrl": { "url": "https://finance.yahoo.com/news/story-1.html" }
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

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ncpJson)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        return new ApiNewsSource(
            httpClient,
            new AppOptions
            {
                FetchBody = fetchBody
            },
            new FakeNewsRepository(areAllKnown),
            new UrlNormalizer(),
            NullLogger<ApiNewsSource>.Instance);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    private sealed class FakeNewsRepository : INewsRepository
    {
        private readonly bool _areAllKnown;

        public FakeNewsRepository(bool areAllKnown)
        {
            _areAllKnown = areAllKnown;
        }

        public Task CheckConnectionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task EnsureSchemaAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> AreAllKnownAsync(IReadOnlyCollection<NewsItem> items, CancellationToken cancellationToken) => Task.FromResult(_areAllKnown);
        public Task<PersistResult> PersistNewsAsync(IReadOnlyCollection<NewsItem> items, CancellationToken cancellationToken) => Task.FromResult(new PersistResult());
        public Task<IReadOnlyCollection<StoredNews>> GetRowsMissingArticleBodyAsync(IReadOnlyCollection<NewsItem> items, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyCollection<StoredNews>>(Array.Empty<StoredNews>());
        public Task UpdateArticleBodyAsync(long newsId, string body, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
