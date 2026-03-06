using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace YahooFinanceIngestor.Tests;

[TestClass]
public sealed class ProgramTests
{
    [TestMethod]
    public async Task Main_ReturnsZeroForHelp()
    {
        var originalError = Console.Error;
        await using var writer = new StringWriter();

        Console.SetError(writer);
        try
        {
            var exitCode = await YahooFinanceIngestor.Program.Main(["--help"]);

            Assert.AreEqual(0, exitCode);
            StringAssert.Contains(writer.ToString(), "--source api|playwright");
        }
        finally
        {
            Console.SetError(originalError);
        }
    }
}
