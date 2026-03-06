using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using YahooFinanceIngestor.Cli;
using YahooFinanceIngestor.Infrastructure;
using YahooFinanceIngestor.Models;
using YahooFinanceIngestor.Services;

namespace YahooFinanceIngestor.Tests;

[TestClass]
public sealed class ArticleBodyFetcherTests
{
    [TestMethod]
    public async Task BackfillBodiesAsync_UsesYahooNewsEndpoint_WhenSupported()
    {
        var requests = new List<Uri>();
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!);

            if (request.RequestUri!.AbsolutePath == "/xhr/news")
            {
                const string json = """
                    {
                      "items": [
                        {
                          "data": {
                            "contentMeta": {
                              "storyAtoms": [
                                { "content": "First paragraph" },
                                { "content": "Second paragraph" }
                              ]
                            }
                          }
                        }
                      ]
                    }
                    """;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var repository = new FakeNewsRepository();
        var fetcher = new ArticleBodyFetcher(
            httpClient,
            repository,
            new AppOptions { BodyConcurrency = 1 },
            NullLogger<ArticleBodyFetcher>.Instance);

        var loaded = await fetcher.BackfillBodiesAsync(
            [new StoredNews { Id = 7, Url = "https://finance.yahoo.com/news/sample-story-123.html" }],
            CancellationToken.None);

        Assert.AreEqual(1, loaded);
        Assert.AreEqual(7L, repository.LastUpdatedId);
        Assert.AreEqual("First paragraph\nSecond paragraph", repository.LastUpdatedBody);
        Assert.HasCount(1, requests);
        Assert.AreEqual("/xhr/news", requests[0].AbsolutePath);
        StringAssert.Contains(requests[0].Query, "url=news%2Fsample-story-123.html");
    }

    [TestMethod]
    public async Task BackfillBodiesAsync_FallsBackToHtml_WhenYahooNewsEndpointFails()
    {
        var requests = new List<Uri>();
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!);

            if (request.RequestUri!.AbsolutePath == "/xhr/news")
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            if (request.RequestUri.AbsolutePath == "/news/sample-story-123.html")
            {
                const string html = """
                    <html>
                      <body>
                        <p>First paragraph with enough text to pass the extractor threshold for article bodies.</p>
                        <p>Second paragraph with enough text to pass the extractor threshold for article bodies.</p>
                      </body>
                    </html>
                    """;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(html)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var repository = new FakeNewsRepository();
        var fetcher = new ArticleBodyFetcher(
            httpClient,
            repository,
            new AppOptions { BodyConcurrency = 1 },
            NullLogger<ArticleBodyFetcher>.Instance);

        var loaded = await fetcher.BackfillBodiesAsync(
            [new StoredNews { Id = 11, Url = "https://finance.yahoo.com/news/sample-story-123.html" }],
            CancellationToken.None);

        Assert.AreEqual(1, loaded);
        Assert.AreEqual(11L, repository.LastUpdatedId);
        Assert.AreEqual(
            "First paragraph with enough text to pass the extractor threshold for article bodies. Second paragraph with enough text to pass the extractor threshold for article bodies.",
            repository.LastUpdatedBody);
        Assert.HasCount(2, requests);
        Assert.AreEqual("/xhr/news", requests[0].AbsolutePath);
        Assert.AreEqual("/news/sample-story-123.html", requests[1].AbsolutePath);
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
        public long? LastUpdatedId { get; private set; }
        public string? LastUpdatedBody { get; private set; }

        public Task CheckConnectionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task EnsureSchemaAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> AreAllKnownAsync(IReadOnlyCollection<NewsItem> items, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<PersistResult> PersistNewsAsync(IReadOnlyCollection<NewsItem> items, CancellationToken cancellationToken) => Task.FromResult(new PersistResult());
        public Task<IReadOnlyCollection<StoredNews>> GetRowsMissingArticleBodyAsync(IReadOnlyCollection<NewsItem> items, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyCollection<StoredNews>>(Array.Empty<StoredNews>());

        public Task UpdateArticleBodyAsync(long newsId, string body, CancellationToken cancellationToken)
        {
            LastUpdatedId = newsId;
            LastUpdatedBody = body;
            return Task.CompletedTask;
        }
    }
}
