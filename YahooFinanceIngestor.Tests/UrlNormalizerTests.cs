using Microsoft.VisualStudio.TestTools.UnitTesting;
using YahooFinanceIngestor.Infrastructure;

namespace YahooFinanceIngestor.Tests;

[TestClass]
public sealed class UrlNormalizerTests
{
    private readonly UrlNormalizer _urlNormalizer = new();

    [TestMethod]
    public void Normalize_RemovesTrackingParameters_SortsQuery_AndCleansCase()
    {
        var normalized = _urlNormalizer.Normalize(
            "HTTPS://Finance.Yahoo.com/topic/latest-news/?b=2&utm_source=test&a=1&guce_referrer=skip#fragment");

        Assert.AreEqual("https://finance.yahoo.com/topic/latest-news?a=1&b=2", normalized);
    }

    [TestMethod]
    public void Normalize_ReturnsEmptyString_ForInvalidUrl()
    {
        var normalized = _urlNormalizer.Normalize("not-a-url");

        Assert.AreEqual(string.Empty, normalized);
    }
}
