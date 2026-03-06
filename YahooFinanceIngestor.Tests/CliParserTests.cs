using Microsoft.VisualStudio.TestTools.UnitTesting;
using YahooFinanceIngestor.Cli;

namespace YahooFinanceIngestor.Tests;

[TestClass]
public sealed class CliParserTests
{
    [TestMethod]
    public void Parse_UsesPlaywrightByDefault()
    {
        var options = CliParser.Parse([]);

        Assert.AreEqual(SourceMode.Playwright, options.Source);
        Assert.AreEqual(250, options.ApiPageSize);
        Assert.AreEqual(4, options.BodyConcurrency);
        Assert.IsTrue(options.Headless);
        Assert.IsTrue(options.PlaywrightApiBackfill);
    }

    [TestMethod]
    public void Parse_ReadsCustomApiOptions()
    {
        var options = CliParser.Parse([
            "--source", "api",
            "--api-page-size", "120",
            "--body-concurrency", "6",
            "--headless", "false"
        ]);

        Assert.AreEqual(SourceMode.Api, options.Source);
        Assert.AreEqual(120, options.ApiPageSize);
        Assert.AreEqual(6, options.BodyConcurrency);
        Assert.IsFalse(options.Headless);
    }

    [TestMethod]
    public void Parse_ReadsCheckDbFlag()
    {
        var options = CliParser.Parse([
            "--check-db",
            "--connection-string", "Host=db;Port=5432;Database=test;Username=user;Password=pass"
        ]);

        Assert.IsTrue(options.CheckDb);
        Assert.AreEqual("Host=db;Port=5432;Database=test;Username=user;Password=pass", options.ConnectionString);
    }

    [TestMethod]
    public void Parse_DisablesKnownPageStop_WhenIgnoreKnownPageFlagIsPresent()
    {
        var options = CliParser.Parse([
            "--ignore-known-page"
        ]);

        Assert.IsFalse(options.StopOnKnownPage);
    }

    [TestMethod]
    public void Parse_DisablesPlaywrightApiBackfill_WhenBrowserOnlyFlagIsPresent()
    {
        var options = CliParser.Parse([
            "--playwright-browser-only"
        ]);

        Assert.IsFalse(options.PlaywrightApiBackfill);
    }
}
