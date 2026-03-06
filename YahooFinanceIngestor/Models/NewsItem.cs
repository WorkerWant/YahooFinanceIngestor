namespace YahooFinanceIngestor.Models;

internal sealed class NewsItem
{
    public string? ExternalId { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public string? Provider { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public string? Summary { get; init; }
    public required string FetchedVia { get; init; }

    public string NormalizedUrl { get; set; } = string.Empty;
}
