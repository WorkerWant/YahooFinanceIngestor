namespace YahooFinanceIngestor.Models;

internal sealed class NcpPageResult
{
    public required IReadOnlyCollection<NewsItem> Items { get; init; }
    public bool NextPage { get; init; }
    public string? PaginationToken { get; init; }
}
