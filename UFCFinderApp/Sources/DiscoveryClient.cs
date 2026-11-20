using System.Text.RegularExpressions;

namespace UFCFinderApp.Sources;

/// <summary>
/// Finds candidate pages the analyst does not already know about, instead of relying only
/// on the hand-maintained watch list. Queries are plain web-search syntax, so the analyst
/// can widen or narrow them without touching the app.
/// </summary>
public sealed class DiscoveryClient
{
    private static readonly Regex AnchorTag = new(
        @"<a\b(?<attrs>[^>]*)>(?<text>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ResultClass = new(
        @"class\s*=\s*""[^""]*result__a[^""]*""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HrefAttribute = new(
        @"href\s*=\s*(?:""(?<href>[^""]+)""|'(?<href>[^']+)')",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpContentLoader _http;

    public DiscoveryClient(HttpContentLoader http) => _http = http;

    /// <summary>
    /// Runs the queries and returns the social-page URLs they surface. Failures are
    /// swallowed on purpose: discovery is an enhancement, and a blocked search engine
    /// should never abort a scan that is otherwise working.
    /// </summary>
    public async Task<IReadOnlyList<Uri>> DiscoverAsync(
        IEnumerable<string> queries,
        IReadOnlyCollection<string> alreadyKnown,
        int maxPerQuery,
        CancellationToken cancellationToken)
    {
        var found = new List<Uri>();
        var seen = new HashSet<string>(alreadyKnown, StringComparer.OrdinalIgnoreCase);

        foreach (var query in queries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<Uri> hits;

            try
            {
                hits = await SearchAsync(query, maxPerQuery, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                continue;
            }

            foreach (var hit in hits)
            {
                var key = hit.GetLeftPart(UriPartial.Path);

                if (seen.Add(key)) found.Add(hit);
            }

            await Task.Delay(1200, cancellationToken).ConfigureAwait(true);
        }

        return found;
    }

    private async Task<List<Uri>> SearchAsync(string query, int max, CancellationToken cancellationToken)
    {
        var url = "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query);
        var response = await _http.Client.GetAsync(url, cancellationToken).ConfigureAwait(true);

        if (!response.IsSuccessStatusCode) return new List<Uri>();

        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
        var results = new List<Uri>();

        foreach (Match anchor in AnchorTag.Matches(html))
        {
            if (results.Count >= max) break;

            if (!ResultClass.IsMatch(anchor.Groups["attrs"].Value)) continue;

            var href = HrefAttribute.Match(anchor.Groups["attrs"].Value);

            if (!href.Success) continue;

            var target = Unwrap(href.Groups["href"].Value);

            if (target is null) continue;

            if (results.All(r => !string.Equals(r.AbsoluteUri, target.AbsoluteUri, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(target);
            }
        }

        return results;
    }

    /// <summary>DuckDuckGo wraps outbound links in a redirect; pull the real URL back out.</summary>
    private static Uri? Unwrap(string href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;

        var value = href.Trim();

        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            value = "https:" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;

        if (uri.Host.Contains("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
        {
            var query = uri.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .FirstOrDefault(pair => pair.Length == 2
                                        && pair[0].Equals("uddg", StringComparison.OrdinalIgnoreCase));

            if (query is not null && Uri.TryCreate(Uri.UnescapeDataString(query[1]), UriKind.Absolute, out var real))
            {
                uri = real;
            }
        }

        return uri.Scheme is "http" or "https" ? uri : null;
    }
}

/// <summary>URL templates that turn an event phrase into the right kind of query.</summary>
public static class TargetUrls
{
    /// <summary>
    /// The Meta Ad Library is the single best public source for this job: it is a
    /// searchable, public index of advertisements rather than a feed that has to be
    /// scrolled. Anyone advertising an event is likely to show up here.
    /// </summary>
    public static string AdLibrary(string phrase, string country = "US") =>
        "https://www.facebook.com/ads/library/?active_status=active&ad_type=all"
        + "&country=" + Uri.EscapeDataString(country)
        + "&q=" + Uri.EscapeDataString(phrase)
        + "&search_type=keyword_unordered&media_type=all";

    /// <summary>Search-engine queries that surface social posts about an event.</summary>
    public static IEnumerable<string> DiscoveryQueries(string phrase)
    {
        yield return $"site:facebook.com \"{phrase}\"";
        yield return $"site:facebook.com/groups \"{phrase}\"";
        yield return $"site:instagram.com \"{phrase}\"";
    }
}
