using Npgsql;
using YahooFinanceIngestor.Cli;
using YahooFinanceIngestor.Infrastructure;
using YahooFinanceIngestor.Models;

namespace YahooFinanceIngestor.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PostgresNewsRepositoryIntegrationTests
{
    private const string TestConnectionVariable = "YAHOO_TEST_PG_CONNECTION";

    private string _connectionString = string.Empty;
    private PostgresNewsRepository _repository = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _connectionString = Environment.GetEnvironmentVariable(TestConnectionVariable) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            Assert.Inconclusive($"{TestConnectionVariable} is not configured.");
        }

        _repository = new PostgresNewsRepository(new AppOptions
        {
            ConnectionString = _connectionString
        });

        await _repository.EnsureSchemaAsync(CancellationToken.None);
        await ResetNewsTableAsync();
    }

    [TestMethod]
    public async Task PersistNewsAsync_InsertsMoreThanOneBatch()
    {
        var items = Enumerable.Range(1, 650)
            .Select(index => CreateItem(index))
            .ToArray();

        var result = await _repository.PersistNewsAsync(items, CancellationToken.None);

        Assert.AreEqual(650, result.Total);
        Assert.AreEqual(650, result.Inserted);
        Assert.AreEqual(0, result.Skipped);
        Assert.HasCount(650, result.InsertedRows);
        Assert.AreEqual(650, await CountRowsAsync());
    }

    [TestMethod]
    public async Task PersistNewsAsync_SkipsDuplicatesAgainstExistingRows()
    {
        var seeded = new[]
        {
            CreateItem(1),
            CreateItem(2)
        };

        await _repository.PersistNewsAsync(seeded, CancellationToken.None);

        var duplicateExternalId = CloneItem(
            CreateItem(3),
            externalId: seeded[0].ExternalId,
            normalizedUrl: "https://finance.yahoo.com/news/new-url-external-duplicate.html");

        var duplicateNormalizedUrl = CloneItem(
            CreateItem(4),
            externalId: "fresh-external-id",
            normalizedUrl: seeded[1].NormalizedUrl);

        var fresh = CreateItem(5);

        var result = await _repository.PersistNewsAsync(
            [duplicateExternalId, duplicateNormalizedUrl, fresh],
            CancellationToken.None);

        Assert.AreEqual(3, result.Total);
        Assert.AreEqual(1, result.Inserted);
        Assert.AreEqual(2, result.Skipped);
        Assert.HasCount(1, result.InsertedRows);
        Assert.AreEqual(fresh.Url, result.InsertedRows.Single().Url);
        Assert.AreEqual(3, await CountRowsAsync());
    }

    [TestMethod]
    public async Task AreAllKnownAsync_ReturnsTrue_WhenEveryItemMatchesEitherKey()
    {
        var seeded = new[]
        {
            CreateItem(1),
            CreateItem(2)
        };

        await _repository.PersistNewsAsync(seeded, CancellationToken.None);

        var lookupItems = new[]
        {
            CloneItem(
                CreateItem(101),
                externalId: seeded[0].ExternalId,
                normalizedUrl: "https://finance.yahoo.com/news/lookup-by-external-id.html"),
            CloneItem(
                CreateItem(102),
                externalId: null,
                normalizedUrl: seeded[1].NormalizedUrl)
        };

        var known = await _repository.AreAllKnownAsync(lookupItems, CancellationToken.None);

        Assert.IsTrue(known);
    }

    [TestMethod]
    public async Task AreAllKnownAsync_ReturnsFalse_WhenAnyItemIsUnknown()
    {
        var seeded = new[]
        {
            CreateItem(1)
        };

        await _repository.PersistNewsAsync(seeded, CancellationToken.None);

        var lookupItems = new[]
        {
            CloneItem(
                CreateItem(201),
                externalId: seeded[0].ExternalId,
                normalizedUrl: "https://finance.yahoo.com/news/known-by-external-id.html"),
            CloneItem(
                CreateItem(202),
                externalId: "missing-external-id",
                normalizedUrl: "https://finance.yahoo.com/news/missing-normalized-url.html")
        };

        var known = await _repository.AreAllKnownAsync(lookupItems, CancellationToken.None);

        Assert.IsFalse(known);
    }

    [TestMethod]
    public async Task GetRowsMissingArticleBodyAsync_ReturnsOnlyRowsWithoutBodyFromCurrentSet()
    {
        var seeded = new[]
        {
            CreateItem(1),
            CreateItem(2),
            CreateItem(3)
        };

        await _repository.PersistNewsAsync(seeded, CancellationToken.None);
        await _repository.UpdateArticleBodyAsync(1, "Body already present", CancellationToken.None);

        var rows = await _repository.GetRowsMissingArticleBodyAsync(
            [seeded[0], seeded[1], CloneItem(CreateItem(999), externalId: "missing", normalizedUrl: "https://finance.yahoo.com/news/missing.html")],
            CancellationToken.None);

        Assert.HasCount(1, rows);
        Assert.AreEqual(seeded[1].Url, rows.Single().Url);
    }

    private async Task ResetNewsTableAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "TRUNCATE TABLE news RESTART IDENTITY;";
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> CountRowsAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM news;";
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    private static NewsItem CreateItem(int index)
    {
        return new NewsItem
        {
            ExternalId = $"story-{index}",
            Title = $"Story {index}",
            Url = $"https://finance.yahoo.com/news/story-{index}.html",
            Provider = "Yahoo Finance",
            PublishedAt = DateTimeOffset.Parse("2026-03-06T12:00:00Z").AddMinutes(index),
            Summary = $"Summary {index}",
            FetchedVia = "api",
            NormalizedUrl = $"https://finance.yahoo.com/news/story-{index}.html"
        };
    }

    private static NewsItem CloneItem(
        NewsItem source,
        string? externalId = null,
        string? normalizedUrl = null)
    {
        return new NewsItem
        {
            ExternalId = externalId,
            Title = source.Title,
            Url = source.Url,
            Provider = source.Provider,
            PublishedAt = source.PublishedAt,
            Summary = source.Summary,
            FetchedVia = source.FetchedVia,
            NormalizedUrl = normalizedUrl ?? source.NormalizedUrl
        };
    }
}
