using YahooFinanceIngestor.Models;

namespace YahooFinanceIngestor.Infrastructure;

internal interface INewsRepository
{
    Task CheckConnectionAsync(CancellationToken cancellationToken);
    Task EnsureSchemaAsync(CancellationToken cancellationToken);
    Task<bool> AreAllKnownAsync(IReadOnlyCollection<NewsItem> items, CancellationToken cancellationToken);
    Task<PersistResult> PersistNewsAsync(IReadOnlyCollection<NewsItem> items, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<StoredNews>> GetRowsMissingArticleBodyAsync(IReadOnlyCollection<NewsItem> items, CancellationToken cancellationToken);
    Task UpdateArticleBodyAsync(long newsId, string body, CancellationToken cancellationToken);
}
