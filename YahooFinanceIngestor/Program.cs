using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Serilog;
using Serilog.Events;
using YahooFinanceIngestor.Cli;
using YahooFinanceIngestor.Infrastructure;
using YahooFinanceIngestor.Services;
using YahooFinanceIngestor.Sources;

namespace YahooFinanceIngestor;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        AppOptions options;
        try
        {
            options = CliParser.Parse(args);
        }
        catch (Exception ex)
        {
            if (string.Equals(ex.Message, CliParser.Usage, StringComparison.Ordinal))
            {
                Console.Error.WriteLine(CliParser.Usage);
                return 0;
            }

            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(CliParser.Usage);
            return 2;
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(MapLogLevel(options.LogLevel))
            .WriteTo.Console()
            .CreateLogger();

        try
        {
            if (options.InstallBrowser)
            {
                return Microsoft.Playwright.Program.Main(["install", "--with-deps", "chromium"]);
            }

            using var cancellationTokenSource = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellationTokenSource.Cancel();
            };

            using IHost host = Host.CreateDefaultBuilder(args)
                .UseSerilog()
                .ConfigureServices(services =>
                {
                    services.AddSingleton(options);
                    services.AddSingleton(new HttpClient(new SocketsHttpHandler
                    {
                        AutomaticDecompression = DecompressionMethods.All,
                        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
                    })
                    {
                        Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
                    });

                    services.AddSingleton<UrlNormalizer>();
                    services.AddSingleton<INewsRepository, PostgresNewsRepository>();
                    services.AddSingleton<ApiNewsSource>();
                    services.AddSingleton<PlaywrightNewsSource>();
                    services.AddSingleton<ArticleBodyFetcher>();
                    services.AddSingleton<IngestionOrchestrator>();
                })
                .Build();

            var orchestrator = host.Services.GetRequiredService<IngestionOrchestrator>();
            var result = await orchestrator.RunAsync(cancellationTokenSource.Token);
            return result;
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Operation cancelled");
            return 130;
        }
        catch (Exception ex)
        {
            var containerDbHint = TryGetContainerDatabaseHint(ex, options.ConnectionString);
            if (!string.IsNullOrWhiteSpace(containerDbHint))
            {
                Log.Error("{Hint}", containerDbHint);
            }

            Log.Fatal(ex, "Unhandled fatal error");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static LogEventLevel MapLogLevel(AppLogLevel level)
    {
        return level switch
        {
            AppLogLevel.Trace => LogEventLevel.Verbose,
            AppLogLevel.Debug => LogEventLevel.Debug,
            AppLogLevel.Warning => LogEventLevel.Warning,
            AppLogLevel.Error => LogEventLevel.Error,
            _ => LogEventLevel.Information
        };
    }

    private static string? TryGetContainerDatabaseHint(Exception ex, string connectionString)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (ex is not NpgsqlException && ex.GetBaseException() is not NpgsqlException)
        {
            return null;
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (!string.Equals(builder.Host, "localhost", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(builder.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }
        catch
        {
            return null;
        }

        return "The app is running inside a container, so Host=localhost points to the app container itself. For Docker Desktop use Host=host.docker.internal; for Docker Compose use the database service name on the same network.";
    }
}
