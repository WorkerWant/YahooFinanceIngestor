namespace YahooFinanceIngestor.Infrastructure;

internal sealed class UrlNormalizer
{
    private static readonly HashSet<string> RemovedQueryParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "guccounter",
        "guce_referrer",
        "guce_referrer_sig",
        "yptr"
    };

    public string Normalize(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.Host.ToLowerInvariant(),
            Fragment = string.Empty
        };

        var path = builder.Path;
        if (path.Length > 1 && path.EndsWith('/'))
        {
            path = path.TrimEnd('/');
        }

        builder.Path = string.IsNullOrWhiteSpace(path) ? "/" : path;

        var queryPairs = ParseQuery(builder.Query)
            .Where(pair =>
                !pair.Key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) &&
                !RemovedQueryParams.Contains(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ThenBy(pair => pair.Value, StringComparer.Ordinal)
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")
            .ToArray();

        builder.Query = queryPairs.Length == 0 ? string.Empty : string.Join("&", queryPairs);
        return builder.Uri.ToString();
    }

    private static IEnumerable<(string Key, string Value)> ParseQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<(string Key, string Value)>();
        }

        var raw = query.StartsWith('?') ? query[1..] : query;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<(string Key, string Value)>();
        }

        var result = new List<(string Key, string Value)>();
        var parts = raw.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var idx = part.IndexOf('=');
            if (idx <= 0)
            {
                var key = Uri.UnescapeDataString(part);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    result.Add((key, string.Empty));
                }

                continue;
            }

            var keyPart = part[..idx];
            var valuePart = idx < part.Length - 1 ? part[(idx + 1)..] : string.Empty;
            var decodedKey = Uri.UnescapeDataString(keyPart);
            var decodedValue = Uri.UnescapeDataString(valuePart);

            if (!string.IsNullOrWhiteSpace(decodedKey))
            {
                result.Add((decodedKey, decodedValue));
            }
        }

        return result;
    }
}
