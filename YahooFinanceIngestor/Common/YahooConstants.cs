namespace YahooFinanceIngestor.Common;

internal static class YahooConstants
{
    public const string TopicPageUrl = "https://finance.yahoo.com/topic/latest-news/";
    public const string EditorialTopicsUrl = "https://finance.yahoo.com/xhr/cds?feature=awsCds&lang=en-US&region=US&schemaId=yfinance%3AeditorialTopics&site=finance-neo";
    public const string TopicsDetailFeedUrl = "https://finance.yahoo.com/xhr/ncp?location=US&queryRef=topicsDetailFeed&serviceKey=ncp_fin&lang=en-US&region=US";
    public const string NewsDetailsUrl = "https://finance.yahoo.com/xhr/news";
    public const string NewsDetailsFeatures = "allowSendingDomainPathToNCP,disableTranscripts,enableXrayNcp,finRedesignMorpheus,filter3pAtoms,filterNonGdprEmbeds,noRapidClickClass";

    public const string UserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/145.0.0.0 Safari/537.36";

    public static readonly string[] BlockedDomains =
    {
        "doubleclick.net",
        "google-analytics.com",
        "googletagmanager.com",
        "googlesyndication.com",
        "taboola.com",
        "criteo.com",
        "adsrvr.org",
        "adservice.google",
        "adnxs.com",
        "chartbeat.com",
        "chartbeat.net",
        "clean.gg",
        "scorecardresearch.com",
        "beacon/batch"
    };
}
