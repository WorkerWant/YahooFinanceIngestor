using YahooFinanceIngestor.Models;

namespace YahooFinanceIngestor.Sources;

internal interface INewsSource
{
    string Name { get; }
    Task<IReadOnlyCollection<NewsItem>> FetchAsync(CancellationToken cancellationToken);
}
