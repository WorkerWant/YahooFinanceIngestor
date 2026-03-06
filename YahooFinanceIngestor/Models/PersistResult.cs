namespace YahooFinanceIngestor.Models;

internal sealed class PersistResult
{
    public int Total { get; init; }
    public int Inserted { get; init; }
    public int Skipped { get; init; }
    public IReadOnlyCollection<StoredNews> InsertedRows { get; init; } = Array.Empty<StoredNews>();
}
