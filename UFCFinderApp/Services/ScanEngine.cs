using UFCFinderApp.Matching;
using UFCFinderApp.Models;
using UFCFinderApp.Sources;

namespace UFCFinderApp.Services;

public sealed class ScanProgress
{
    public int Done { get; init; }
    public int Total { get; init; }
    public string Message { get; init; } = string.Empty;

    /// <summary>Results produced since the previous report, so the UI can fill in live.</summary>
    public IReadOnlyList<ScanFinding> NewFindings { get; init; } = Array.Empty<ScanFinding>();

    public IReadOnlyList<LegalMatch> NewLegalMatches { get; init; } = Array.Empty<LegalMatch>();

    public IReadOnlyList<ScanError> NewErrors { get; init; } = Array.Empty<ScanError>();
}

public sealed class ScanRequest
{
    public List<string> Phrases { get; init; } = new();
    public List<LegalVenue> LegalVenues { get; init; } = new();
    public List<WatchTarget> Targets { get; init; } = new();

    /// <summary>Search the Meta Ad Library for each phrase.</summary>
    public bool EnableAdLibrary { get; init; } = true;

    /// <summary>Run web searches to surface pages that are not on the watch list.</summary>
    public bool EnableDiscovery { get; init; } = true;

    /// <summary>Scroll endlessly-loaded feeds so more than the first screen of posts is read.</summary>
    public bool CollectScrollingPosts { get; init; } = true;

    /// <summary>Load Facebook pages in the mobile layout, which exposes more of a feed.</summary>
    public bool UseMobileView { get; init; }

    public int DelayBetweenPagesMs { get; init; } = 2000;
}

/// <summary>
/// Runs a scan end to end: filter legal venues, plan the work (watch list, ad library,
/// discovery), visit each URL, score what was said, and assemble the report.
/// </summary>
public sealed class ScanEngine
{
    /// <summary>
    /// Domains belonging to rights holders and licensed broadcasters. Pages on these hosts
    /// are never a lead, so they are filtered the same way a legal-list address is.
    /// </summary>
    private static readonly string[] LegalDomains =
    {
        "ufc.com", "ufc.tv", "ufcfightpass.com", "espn.com", "espnplus.com", "dazn.com",
        "tsn.ca", "sky.com", "btsport.com", "tntsports.com", "ticketmaster.com",
        "axs.com", "stubhub.com", "seatgeek.com"
    };

    private const int MaxAdLibraryPhrases = 3;
    private const int MaxDiscoveryQueries = 6;
    private const int MaxDiscoveryResultsPerQuery = 6;

    private readonly BrowserContentLoader? _browser;
    private readonly HttpContentLoader _http;
    private readonly DiscoveryClient _discovery;

    /// <param name="browser">
    /// May be null. Without a browser session the engine still scans everything that is
    /// reachable over plain HTTP; only signed-in sites are skipped.
    /// </param>
    public ScanEngine(BrowserContentLoader? browser, HttpContentLoader http)
    {
        _browser = browser;
        _http = http;
        _discovery = new DiscoveryClient(http);
    }

    public async Task<ScanReport> RunAsync(
        ScanRequest request,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var matcher = new PhraseMatcher(request.Phrases);

        if (matcher.Count == 0)
        {
            throw new InvalidOperationException("At least one search phrase is required.");
        }

        var report = new ScanReport { StartedAt = DateTimeOffset.Now };

        var work = new List<WorkItem>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddUrl(WatchTarget target, string url, bool discovered)
        {
            var trimmed = NormalizeUrl(url);

            if (trimmed.Length == 0) return;
            if (!seenUrls.Add(trimmed)) return;

            work.Add(new WorkItem(target, trimmed, discovered));
        }

        var legalKeys = new HashSet<string>(
            request.LegalVenues.Select(v => v.LocationKey),
            StringComparer.OrdinalIgnoreCase);

        // 1. The hand-maintained watch list, minus anything already known to be legal.
        foreach (var target in request.Targets)
        {
            if (legalKeys.Contains(target.LocationKey))
            {
                report.LegalMatches.Add(new LegalMatch
                {
                    Business = target.Business,
                    AddressLine = target.AddressLine,
                    Reason = "Matches a legal venue on the legal list"
                });

                continue;
            }

            foreach (var url in target.Urls)
            {
                if (IsLegalDomain(url))
                {
                    report.LegalMatches.Add(new LegalMatch
                    {
                        Business = target.Business,
                        AddressLine = target.AddressLine,
                        Reason = "Official rights-holder or broadcaster domain"
                    });

                    continue;
                }

                AddUrl(target, url, discovered: false);
            }
        }

        // 2. The Meta Ad Library, which indexes advertisements directly.
        if (request.EnableAdLibrary)
        {
            foreach (var phrase in matcher.Phrases.Take(MaxAdLibraryPhrases))
            {
                var target = new WatchTarget { Business = $"Meta Ad Library: \"{phrase}\"" };
                AddUrl(target, TargetUrls.AdLibrary(phrase), discovered: false);
            }
        }

        // 3. Web search discovery for pages nobody has listed yet.
        if (request.EnableDiscovery)
        {
            progress?.Report(new ScanProgress
            {
                Done = 0,
                Total = work.Count,
                Message = "Searching the web for pages that are not on the watch list…"
            });

            var queries = matcher.Phrases
                .Take(2)
                .SelectMany(TargetUrls.DiscoveryQueries)
                .Take(MaxDiscoveryQueries)
                .ToList();

            var known = work.Select(w => w.Url).ToList();

            IReadOnlyList<Uri> discovered;

            try
            {
                discovered = await _discovery
                    .DiscoverAsync(queries, known, MaxDiscoveryResultsPerQuery, cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                report.Errors.Add(new ScanError { Url = "web search", Message = ex.Message });
                discovered = Array.Empty<Uri>();
            }

            foreach (var page in discovered)
            {
                if (IsLegalDomain(page.ToString())) continue;

                var label = page.Host.Replace("www.", string.Empty, StringComparison.OrdinalIgnoreCase)
                            + page.AbsolutePath;

                AddUrl(WatchTarget.ForDiscoveredPage(page.ToString(), label), page.ToString(), discovered: true);
            }
        }

        report.UrlsConsidered = work.Count;
        var processed = 0;

        // Show anything already filtered before the first page is even fetched.
        progress?.Report(new ScanProgress
        {
            Done = 0,
            Total = work.Count,
            Message = $"Planned {work.Count} URL(s).",
            NewLegalMatches = report.LegalMatches.ToList()
        });

        // 4. Visit every planned URL and score what is on the page. Findings are published
        //    as they are produced, so the grid fills in during the scan and cancelling
        //    keeps everything found up to that point.
        foreach (var item in work)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pageFindings = new List<ScanFinding>();
            var pageErrors = new List<ScanError>();
            var host = item.Url;

            if (!Uri.TryCreate(item.Url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                pageErrors.Add(new ScanError { Url = item.Url, Message = "Not a valid http(s) URL." });
            }
            else
            {
                host = uri.Host;

                progress?.Report(new ScanProgress
                {
                    Done = processed,
                    Total = work.Count,
                    Message = $"Scanning {uri.Host}…"
                });

                try
                {
                    var document = await LoadAsync(uri, request, cancellationToken).ConfigureAwait(true);

                    if (document is null || !document.HasContent)
                    {
                        pageErrors.Add(new ScanError
                        {
                            Url = uri.ToString(),
                            Message = "Nothing could be read (blocked, sign-in required, or no visible content)."
                        });
                    }
                    else if (LooksLikeSignInWall(document))
                    {
                        pageErrors.Add(new ScanError
                        {
                            Url = uri.ToString(),
                            Message = "Sign-in required — sign in to the site in the browser panel, then run the scan again."
                        });
                    }
                    else
                    {
                        report.PagesScanned++;
                        pageFindings = Evaluate(document, item, matcher, report);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    pageErrors.Add(new ScanError { Url = uri.ToString(), Message = ex.Message });
                }
            }

            processed++;

            report.Findings.AddRange(pageFindings);
            report.Errors.AddRange(pageErrors);

            progress?.Report(new ScanProgress
            {
                Done = processed,
                Total = work.Count,
                Message = BuildProgressMessage(processed, work.Count, host, pageFindings.Count),
                NewFindings = pageFindings,
                NewErrors = pageErrors
            });

            if (request.DelayBetweenPagesMs > 0)
            {
                await Task.Delay(request.DelayBetweenPagesMs, cancellationToken).ConfigureAwait(true);
            }
        }

        report.CompletedAt = DateTimeOffset.Now;

        // Highest-scoring, most clearly commercial leads first.
        var ranked = report.Findings
            .OrderByDescending(f => f.Score)
            .ThenBy(f => f.Business, StringComparer.OrdinalIgnoreCase)
            .ToList();

        report.Findings.Clear();
        report.Findings.AddRange(ranked);

        progress?.Report(new ScanProgress
        {
            Done = processed,
            Total = work.Count,
            Message = $"Finished: {report.Findings.Count} lead(s) from {report.PagesScanned} page(s)."
        });

        return report;
    }

    private async Task<LoadedDocument?> LoadAsync(
        Uri uri,
        ScanRequest request,
        CancellationToken cancellationToken)
    {
        var profile = SourceProfile.For(uri);

        if (profile.RequiresBrowser)
        {
            if (_browser is { IsReady: true })
            {
                var target = request.UseMobileView ? ToMobileUrl(uri) : uri.ToString();

                return await _browser
                    .LoadAsync(target, profile, request.CollectScrollingPosts, cancellationToken)
                    .ConfigureAwait(true);
            }

            return await _http.LoadAsync(uri.ToString(), cancellationToken).ConfigureAwait(true);
        }

        var httpDocument = await _http.LoadAsync(uri.ToString(), cancellationToken).ConfigureAwait(true);

        // A public page reached over HTTP is by far the cheapest path. If it comes back
        // suspiciously empty it was almost certainly rendered by script, so retry it in
        // the browser before giving up on the URL.
        if (httpDocument is not null && httpDocument.Text.Length > 500)
        {
            return httpDocument;
        }

        if (_browser is { IsReady: true })
        {
            var browserDocument = await _browser
                .LoadAsync(uri.ToString(), profile, false, cancellationToken)
                .ConfigureAwait(true);

            if (browserDocument is not null
                && browserDocument.Text.Length > (httpDocument?.Text.Length ?? 0))
            {
                return browserDocument;
            }
        }

        return httpDocument;
    }

    private static List<ScanFinding> Evaluate(
        LoadedDocument document,
        WorkItem item,
        PhraseMatcher matcher,
        ScanReport report)
    {
        var findings = new List<ScanFinding>();

        // Preferred: judge each post on its own so navigation chrome, comment threads and
        // unrelated sidebar text cannot create false positives.
        var units = document.Posts.Count > 0
            ? document.Posts.Select(p => (Text: p.Text, Link: p.Permalink ?? document.Url)).ToList()
            : new List<(string Text, string Link)> { (document.Text, document.Url) };

        foreach (var unit in units)
        {
            if (unit.Text.Length < 10) continue;

            report.PostsScanned++;

            var normalized = TextNormalizer.NormalizeWithMap(unit.Text);
            var matches = matcher.FindAll(normalized);

            if (matches.Count == 0) continue;

            var evidence = EvidenceSelector.Select(unit.Text, normalized, matches);

            // On a long page, score the region around the match rather than the whole
            // document: an unrelated "$50,000" elsewhere on the page must not make a
            // passing mention look like a paid showing.
            var scoringText = unit.Text.Length <= 2000 ? normalized.Text : evidence.NormalizedText;

            var score = FindingScorer.Score(scoringText, matches);

            findings.Add(new ScanFinding
            {
                Business = item.Target.Business,
                AddressLine = item.Target.AddressLine,
                LocationKey = item.Target.LocationKey,
                Source = SourceProfile.LabelFor(unit.Link),
                Url = unit.Link,
                Score = score.Score,
                MatchedPhrases = matches.Select(m => m.Phrase).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Signals = score.Reasons,
                Evidence = evidence.Excerpt,
                Discovered = item.Discovered
            });
        }

        return findings;
    }

    private static string BuildProgressMessage(int processed, int total, string host, int newFindings)
    {
        var message = $"Scanned {processed} of {total} — {host}";

        return newFindings > 0 ? $"{message} · {newFindings} lead(s)" : message;
    }

    /// <summary>
    /// Swaps a Facebook URL for its mobile equivalent. The mobile feed mounts more posts
    /// per screen and keeps them simpler, which is why it surfaced more content before.
    /// The ad library is left alone: it has no mobile keyword view worth using.
    /// </summary>
    private static string ToMobileUrl(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();

        var isFacebook = host.EndsWith("facebook.com", StringComparison.Ordinal)
                         || host.EndsWith("fb.com", StringComparison.Ordinal);

        if (!isFacebook) return uri.ToString();

        if (uri.AbsolutePath.Contains("/ads/library", StringComparison.OrdinalIgnoreCase)) return uri.ToString();
        if (host.StartsWith("m.facebook", StringComparison.Ordinal)) return uri.ToString();

        return new UriBuilder(uri) { Host = "m.facebook.com" }.Uri.ToString();
    }

    /// <summary>Adds the scheme that hand-written spreadsheet URLs usually leave off.</summary>
    private static string NormalizeUrl(string url)
    {
        var trimmed = url.Trim();

        if (trimmed.Length == 0) return trimmed;

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return trimmed.StartsWith("//", StringComparison.Ordinal)
            ? "https:" + trimmed
            : "https://" + trimmed.TrimStart('/');
    }

    /// <summary>
    /// A signed-out Facebook or Instagram visit lands on /login or /checkpoint. Catching
    /// that tells the analyst to sign in instead of quietly reporting zero leads.
    /// </summary>
    private static bool LooksLikeSignInWall(LoadedDocument document)
    {
        if (!Uri.TryCreate(document.Url, UriKind.Absolute, out var uri)) return false;
        if (!SourceProfile.For(uri).RequiresBrowser) return false;

        return uri.AbsolutePath.Contains("login", StringComparison.OrdinalIgnoreCase)
               || uri.AbsolutePath.Contains("checkpoint", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLegalDomain(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;

        var host = uri.Host.ToLowerInvariant();

        return LegalDomains.Any(domain =>
            host.Equals(domain, StringComparison.Ordinal) || host.EndsWith("." + domain, StringComparison.Ordinal));
    }

    private sealed record WorkItem(WatchTarget Target, string Url, bool Discovered);
}
