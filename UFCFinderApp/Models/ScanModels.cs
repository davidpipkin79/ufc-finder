namespace UFCFinderApp.Models;

/// <summary>A venue that is licensed to show the event. Watch-list rows matching one of
/// these are filtered out before any scanning happens.</summary>
public sealed class LegalVenue
{
    public string Business { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string Zip { get; init; } = string.Empty;

    public string LocationKey => Matching.LocationKey.From(this);
}

/// <summary>A suspect business plus every social/web URL that belongs to it.</summary>
public sealed class WatchTarget
{
    public string Business { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string Zip { get; init; } = string.Empty;

    public List<string> Urls { get; init; } = new();

    public string LocationKey => Matching.LocationKey.From(this);

    public string AddressLine
    {
        get
        {
            var cityState = string.Join(", ",
                new[] { City, State }.Where(p => !string.IsNullOrWhiteSpace(p)));

            var tail = string.Join(" ", new[] { cityState, Zip }.Where(p => !string.IsNullOrWhiteSpace(p)));

            return string.IsNullOrWhiteSpace(tail)
                ? Address
                : string.IsNullOrWhiteSpace(Address) ? tail : $"{Address}, {tail}";
        }
    }

    /// <summary>A stand-in target used for pages that discovery found rather than the sheet.</summary>
    public static WatchTarget ForDiscoveredPage(string url, string business) => new()
    {
        Business = business,
        Urls = new List<string> { url }
    };
}

/// <summary>One page or post that matched, with everything an analyst needs to verify it.</summary>
public sealed class ScanFinding
{
    public string Business { get; init; } = string.Empty;
    public string AddressLine { get; init; } = string.Empty;
    public string LocationKey { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public int Score { get; init; }
    public IReadOnlyList<string> MatchedPhrases { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Signals { get; init; } = Array.Empty<string>();
    public string Evidence { get; init; } = string.Empty;
    public bool Discovered { get; init; }

    public string Severity => Score >= 60 ? "High" : Score >= 30 ? "Medium" : "Low";
}

/// <summary>A URL that could not be scanned, kept in the report so gaps are visible.</summary>
public sealed class ScanError
{
    public string Url { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

/// <summary>A watch-list row that was skipped because it is a known legal venue or domain.</summary>
public sealed class LegalMatch
{
    public string Business { get; init; } = string.Empty;
    public string AddressLine { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

public sealed class ScanReport
{
    public List<ScanFinding> Findings { get; } = new();
    public List<LegalMatch> LegalMatches { get; } = new();
    public List<ScanError> Errors { get; } = new();

    public int PagesScanned { get; set; }
    public int PostsScanned { get; set; }
    public int UrlsConsidered { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset CompletedAt { get; set; } = DateTimeOffset.Now;

    public TimeSpan Duration => CompletedAt - StartedAt;
}
