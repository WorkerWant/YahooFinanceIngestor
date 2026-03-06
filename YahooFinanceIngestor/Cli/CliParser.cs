namespace YahooFinanceIngestor.Cli;

internal static class CliParser
{
    public static string Usage =>
        "Usage:\n" +
        "  --source api|playwright (default: playwright)\n" +
        "  --log-level trace|debug|info|warn|error\n" +
        "  --fetch-body\n" +
        "  --ignore-known-page\n" +
        "  --playwright-browser-only\n" +
        "  --timeout <seconds>\n" +
        "  --max-items <number>\n" +
        "  --api-page-size <number>\n" +
        "  --body-concurrency <number>\n" +
        "  --connection-string <postgres connection string>\n" +
        "  --check-db\n" +
        "  --dry-run\n" +
        "  --headless true|false\n" +
        "  --install-browser\n" +
        "\nExamples:\n" +
        "  dotnet run -- --check-db --connection-string \"Host=localhost;Port=5432;Database=yahoo_news;Username=postgres;Password=postgres\"\n" +
        "  dotnet run -- --log-level info --timeout 30\n" +
        "  dotnet run -- --source playwright --headless true --max-items 200\n" +
        "  dotnet run -- --source api --api-page-size 250 --fetch-body --body-concurrency 6";

    public static AppOptions Parse(string[] args)
    {
        var source = SourceMode.Playwright;
        var logLevel = AppLogLevel.Information;
        var fetchBody = false;
        var timeout = 30;
        int? maxItems = null;
        var apiPageSize = 250;
        string? connectionString = null;
        var dryRun = false;
        var headless = true;
        var bodyConcurrency = 4;
        var installBrowser = false;
        var checkDb = false;
        var stopOnKnownPage = true;
        var playwrightApiBackfill = true;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--help":
                case "-h":
                    throw new ArgumentException(Usage);

                case "--source":
                    source = ParseSource(GetRequiredValue(args, ref i, "--source"));
                    break;

                case "--fetch-body":
                    fetchBody = true;
                    break;

                case "--ignore-known-page":
                    stopOnKnownPage = false;
                    break;

                case "--playwright-browser-only":
                    playwrightApiBackfill = false;
                    break;

                case "--log-level":
                    logLevel = ParseLogLevel(GetRequiredValue(args, ref i, "--log-level"));
                    break;

                case "--timeout":
                    timeout = ParsePositiveInt(GetRequiredValue(args, ref i, "--timeout"), "--timeout");
                    break;

                case "--max-items":
                    maxItems = ParsePositiveInt(GetRequiredValue(args, ref i, "--max-items"), "--max-items");
                    break;

                case "--api-page-size":
                    apiPageSize = ParseBoundedInt(GetRequiredValue(args, ref i, "--api-page-size"), "--api-page-size", 1, 250);
                    break;

                case "--body-concurrency":
                    bodyConcurrency = ParsePositiveInt(GetRequiredValue(args, ref i, "--body-concurrency"), "--body-concurrency");
                    break;

                case "--connection-string":
                    connectionString = GetRequiredValue(args, ref i, "--connection-string");
                    break;

                case "--dry-run":
                    dryRun = true;
                    break;

                case "--check-db":
                    checkDb = true;
                    break;

                case "--headless":
                    headless = ParseBool(GetRequiredValue(args, ref i, "--headless"), "--headless");
                    break;

                case "--install-browser":
                    installBrowser = true;
                    break;

                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

        return new AppOptions
        {
            Source = source,
            LogLevel = logLevel,
            FetchBody = fetchBody,
            TimeoutSeconds = timeout,
            MaxItems = maxItems,
            ApiPageSize = apiPageSize,
            ConnectionString = string.IsNullOrWhiteSpace(connectionString)
                ? new AppOptions().ConnectionString
                : connectionString,
            DryRun = dryRun,
            Headless = headless,
            BodyConcurrency = bodyConcurrency,
            StopOnKnownPage = stopOnKnownPage,
            PlaywrightApiBackfill = playwrightApiBackfill,
            InstallBrowser = installBrowser,
            CheckDb = checkDb
        };
    }

    private static SourceMode ParseSource(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "api" => SourceMode.Api,
            "playwright" or "pw" => SourceMode.Playwright,
            _ => throw new ArgumentException("--source must be one of: api | playwright")
        };
    }

    private static AppLogLevel ParseLogLevel(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "trace" => AppLogLevel.Trace,
            "debug" => AppLogLevel.Debug,
            "info" or "information" => AppLogLevel.Information,
            "warn" or "warning" => AppLogLevel.Warning,
            "error" => AppLogLevel.Error,
            _ => throw new ArgumentException("--log-level must be one of: trace|debug|info|warn|error")
        };
    }

    private static int ParsePositiveInt(string value, string optionName)
    {
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
        {
            throw new ArgumentException($"{optionName} must be a positive integer");
        }

        return parsed;
    }

    private static int ParseBoundedInt(string value, string optionName, int min, int max)
    {
        var parsed = ParsePositiveInt(value, optionName);
        if (parsed < min || parsed > max)
        {
            throw new ArgumentException($"{optionName} must be between {min} and {max}");
        }

        return parsed;
    }

    private static bool ParseBool(string value, string optionName)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "y" => true,
            "false" or "0" or "no" or "n" => false,
            _ => throw new ArgumentException($"{optionName} must be true|false")
        };
    }

    private static string GetRequiredValue(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {optionName}");
        }

        index++;
        return args[index];
    }
}
