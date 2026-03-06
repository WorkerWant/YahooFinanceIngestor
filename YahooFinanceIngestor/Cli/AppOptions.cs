namespace YahooFinanceIngestor.Cli;

internal sealed class AppOptions
{
    public SourceMode Source { get; init; } = SourceMode.Playwright;
    public AppLogLevel LogLevel { get; init; } = AppLogLevel.Information;
    public bool FetchBody { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
    public int? MaxItems { get; init; }
    public int ApiPageSize { get; init; } = 250;
    public string ConnectionString { get; init; } =
        Environment.GetEnvironmentVariable("YAHOO_PG_CONNECTION")
        ?? "Host=localhost;Port=5432;Database=yahoo_news;Username=postgres;Password=postgres";
    public bool DryRun { get; init; }
    public bool Headless { get; init; } = true;
    public int BodyConcurrency { get; init; } = 4;
    public bool StopOnKnownPage { get; init; } = true;
    public bool PlaywrightApiBackfill { get; init; } = true;
    public bool InstallBrowser { get; init; }
    public bool CheckDb { get; init; }
}
