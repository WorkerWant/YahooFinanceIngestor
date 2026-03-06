using Dapper;
using Npgsql;
using System.Text.Json;
using System.Text.Json.Serialization;
using YahooFinanceIngestor.Cli;
using YahooFinanceIngestor.Models;

namespace YahooFinanceIngestor.Infrastructure;

internal sealed class PostgresNewsRepository : INewsRepository
{
    private const int InsertBatchSize = 500;
    private readonly AppOptions _options;

    public PostgresNewsRepository(AppOptions options)
    {
        _options = options;
    }

    public async Task CheckConnectionAsync(CancellationToken cancellationToken)
    {
        const string sql = "SELECT 1;";

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS news
            (
                id BIGSERIAL PRIMARY KEY,
                external_id TEXT NULL,
                title TEXT NOT NULL,
                url TEXT NOT NULL,
                normalized_url TEXT NOT NULL,
                provider TEXT NULL,
                published_at TIMESTAMPTZ NULL,
                summary TEXT NULL,
                article_body TEXT NULL,
                body_fetched_at TIMESTAMPTZ NULL,
                fetched_via TEXT NOT NULL,
                fetched_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_news_external_id
                ON news(external_id)
                WHERE external_id IS NOT NULL;

            CREATE UNIQUE INDEX IF NOT EXISTS ux_news_normalized_url
                ON news(normalized_url);

            CREATE INDEX IF NOT EXISTS ix_news_published_at
                ON news(published_at DESC NULLS LAST);
            """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    public async Task<bool> AreAllKnownAsync(IReadOnlyCollection<NewsItem> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return false;
        }

        var externalIds = items
            .Select(item => string.IsNullOrWhiteSpace(item.ExternalId) ? null : item.ExternalId.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray()!;

        var normalizedUrls = items
            .Select(item => item.NormalizedUrl)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        const string lookupSql = """
            SELECT
                external_id AS ExternalId,
                normalized_url AS NormalizedUrl
            FROM news
            WHERE external_id = ANY(@ExternalIds)
               OR normalized_url = ANY(@NormalizedUrls);
            """;

        var knownRows = await connection.QueryAsync<KnownKeyRow>(
            new CommandDefinition(
                lookupSql,
                new
                {
                    ExternalIds = externalIds,
                    NormalizedUrls = normalizedUrls
                },
                cancellationToken: cancellationToken));

        var existingExternalIds = knownRows
            .Select(static row => row.ExternalId)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal)!;

        var existingNormalizedUrls = knownRows
            .Select(static row => row.NormalizedUrl)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal)!;

        return items.All(item =>
        {
            var externalId = string.IsNullOrWhiteSpace(item.ExternalId) ? null : item.ExternalId.Trim();
            if (externalId is not null && existingExternalIds.Contains(externalId))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(item.NormalizedUrl) &&
                   existingNormalizedUrls.Contains(item.NormalizedUrl);
        });
    }

    public async Task<PersistResult> PersistNewsAsync(
        IReadOnlyCollection<NewsItem> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return new PersistResult { Total = 0, Inserted = 0, Skipped = 0 };
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string insertSql = """
            WITH input_rows AS
            (
                SELECT *
                FROM jsonb_to_recordset(CAST(@Payload AS jsonb))
                AS batch
                (
                    external_id TEXT,
                    title TEXT,
                    url TEXT,
                    normalized_url TEXT,
                    provider TEXT,
                    published_at TIMESTAMPTZ,
                    summary TEXT,
                    fetched_via TEXT
                )
            ),
            inserted AS
            (
                INSERT INTO news
                (
                    external_id,
                    title,
                    url,
                    normalized_url,
                    provider,
                    published_at,
                    summary,
                    fetched_via,
                    fetched_at
                )
                SELECT
                    external_id,
                    title,
                    url,
                    normalized_url,
                    provider,
                    published_at,
                    summary,
                    fetched_via,
                    NOW()
                FROM input_rows
                ON CONFLICT DO NOTHING
                RETURNING id, url
            )
            SELECT id AS Id, url AS Url
            FROM inserted;
            """;

        var insertedRows = new List<StoredNews>(items.Count);
        var inserted = 0;
        var skipped = 0;

        foreach (var batch in items.Chunk(InsertBatchSize))
        {
            var payload = BuildInsertPayload(batch);
            var rows = await connection.QueryAsync<InsertedRow>(
                new CommandDefinition(
                    insertSql,
                    new { Payload = payload },
                    transaction: transaction,
                    cancellationToken: cancellationToken));

            var insertedBatch = rows
                .Select(static row => new StoredNews
                {
                    Id = row.Id,
                    Url = row.Url
                })
                .ToArray();

            inserted += insertedBatch.Length;
            skipped += batch.Length - insertedBatch.Length;
            insertedRows.AddRange(insertedBatch);
        }

        await transaction.CommitAsync(cancellationToken);

        return new PersistResult
        {
            Total = items.Count,
            Inserted = inserted,
            Skipped = skipped,
            InsertedRows = insertedRows
        };
    }

    public async Task<IReadOnlyCollection<StoredNews>> GetRowsMissingArticleBodyAsync(
        IReadOnlyCollection<NewsItem> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return Array.Empty<StoredNews>();
        }

        var externalIds = items
            .Select(item => string.IsNullOrWhiteSpace(item.ExternalId) ? null : item.ExternalId.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray()!;

        var normalizedUrls = items
            .Select(item => item.NormalizedUrl)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (externalIds.Length == 0 && normalizedUrls.Length == 0)
        {
            return Array.Empty<StoredNews>();
        }

        const string sql = """
            SELECT id AS Id, url AS Url
            FROM news
            WHERE (external_id = ANY(@ExternalIds)
                OR normalized_url = ANY(@NormalizedUrls))
              AND NULLIF(BTRIM(article_body), '') IS NULL;
            """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<StoredNews>(
            new CommandDefinition(
                sql,
                new
                {
                    ExternalIds = externalIds,
                    NormalizedUrls = normalizedUrls
                },
                cancellationToken: cancellationToken));

        return rows.ToArray();
    }

    public async Task UpdateArticleBodyAsync(long newsId, string body, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE news
            SET article_body = @Body,
                body_fetched_at = NOW()
            WHERE id = @Id;
            """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            new CommandDefinition(sql, new { Id = newsId, Body = body }, cancellationToken: cancellationToken));
    }

    private NpgsqlConnection CreateConnection() => new(_options.ConnectionString);

    private static string BuildInsertPayload(NewsItem[] batch)
    {
        var rows = batch.Select(static item => new NewsInsertRow
        {
            ExternalId = item.ExternalId,
            Title = item.Title,
            Url = item.Url,
            NormalizedUrl = item.NormalizedUrl,
            Provider = item.Provider,
            PublishedAt = item.PublishedAt,
            Summary = item.Summary,
            FetchedVia = item.FetchedVia
        });

        return JsonSerializer.Serialize(rows);
    }

    private sealed class KnownKeyRow
    {
        public string? ExternalId { get; set; }
        public string? NormalizedUrl { get; set; }
    }

    private sealed class InsertedRow
    {
        public long Id { get; set; }
        public string Url { get; set; } = string.Empty;
    }

    private sealed class NewsInsertRow
    {
        [JsonPropertyName("external_id")]
        public string? ExternalId { get; init; }

        [JsonPropertyName("title")]
        public string Title { get; init; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; init; } = string.Empty;

        [JsonPropertyName("normalized_url")]
        public string NormalizedUrl { get; init; } = string.Empty;

        [JsonPropertyName("provider")]
        public string? Provider { get; init; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; init; }

        [JsonPropertyName("summary")]
        public string? Summary { get; init; }

        [JsonPropertyName("fetched_via")]
        public string FetchedVia { get; init; } = string.Empty;
    }
}
