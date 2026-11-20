using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace UFCFinderApp.Sources;

/// <summary>
/// Drives the on-screen WebView2 as a scanning surface for sites that need a signed-in
/// session (Facebook, Instagram, the ad library).
///
/// Sessions live in a persistent WebView2 user-data folder, exactly like a normal browser
/// profile. The analyst signs in by hand once; the app never sees or stores a password.
/// </summary>
public sealed class BrowserContentLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Pause before the first look for posts, so the page has painted something.</summary>
    private const int WarmUpDelayMs = 350;

    /// <summary>Ceiling for waiting on a page's first posts; returns as soon as they exist.</summary>
    private const int FirstPostTimeoutMs = 2500;

    private const int FirstPostPollMs = 150;

    /// <summary>How long to keep waiting for the next batch of a feed to appear.</summary>
    private const int FeedGrowthTimeoutMs = 1500;

    private const int FeedGrowthPollMs = 250;

    private readonly WebView2 _view;
    private readonly Dispatcher _dispatcher;

    public BrowserContentLoader(WebView2 view)
    {
        _view = view;
        _dispatcher = view.Dispatcher;
    }

    public WebView2 View => _view;

    public bool IsReady => _view.CoreWebView2 is not null;

    /// <summary>Creates the underlying browser process. Must run on the UI thread.</summary>
    public async Task InitializeAsync(string userDataFolder)
    {
        if (_view.CoreWebView2 is not null) return;

        if (!_dispatcher.CheckAccess())
        {
            throw new InvalidOperationException("The browser session must be initialized on the UI thread.");
        }

        Directory.CreateDirectory(userDataFolder);

        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = "--no-first-run --disable-background-networking"
        };

        var environment = await CoreWebView2Environment
            .CreateAsync(browserExecutableFolder: null, userDataFolder: userDataFolder, options)
            .ConfigureAwait(true);

        await _view.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

        var core = _view.CoreWebView2
            ?? throw new InvalidOperationException("The browser engine did not start.");

        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;
    }

    /// <summary>Navigates to a URL, lets the page settle, scrolls for lazy content, then reads it.</summary>
    public async Task<LoadedDocument?> LoadAsync(
        string url,
        SourceProfile profile,
        bool collectScrollingPosts,
        CancellationToken cancellationToken,
        Action<string>? activity = null)
    {
        var core = _view.CoreWebView2
            ?? throw new InvalidOperationException("The browser session is not ready yet.");

        var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            navigation.TrySetResult(args.IsSuccess);
        }

        core.NavigationCompleted += OnNavigationCompleted;

        try
        {
            core.Navigate(url);

            var navigated = false;

            try
            {
                navigated = await navigation.Task
                    .WaitAsync(TimeSpan.FromSeconds(60), cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (TimeoutException)
            {
                navigated = false;
            }

            if (!navigated) return null;

            activity?.Invoke("waiting for the page to settle");

            // A fixed multi-second settle is wasted time on every page of a long run. Wait
            // for the first posts to mount instead: a responsive page costs a few hundred
            // milliseconds, and a slow one still gets its full ceiling. Pages with nothing
            // to poll for (the ad library) keep their settle delay.
            if (profile.PostSelector is not null)
            {
                await Task.Delay(WarmUpDelayMs, cancellationToken).ConfigureAwait(true);
                await WaitForPostsAsync(core, profile, cancellationToken).ConfigureAwait(true);
            }
            else
            {
                await Task.Delay(profile.SettleDelayMs, cancellationToken).ConfigureAwait(true);
            }

            var shouldScroll = collectScrollingPosts && profile.ScrollPasses > 0;

            if (!shouldScroll)
            {
                return Decode(
                    await ReadPageAsync(core, profile).ConfigureAwait(true),
                    url,
                    profile.DisplayName);
            }

            // Locate the container that really scrolls the feed before moving anything.
            // Without this, pages whose list lives inside an overflow element ignore every
            // window scroll and the harvest quietly stops after the first screenful.
            await core
                .ExecuteScriptAsync(SourceProfile.BuildScrollProbeScript(profile))
                .ConfigureAwait(true);

            if (profile.PostSelector is null)
            {
                // Nothing structured to harvest (for example the ad library). Scrolling is
                // still what makes the page load more cards, so scroll first, then read once.
                await ScrollToBottomAsync(core, profile, cancellationToken, activity).ConfigureAwait(true);

                return Decode(
                    await ReadPageAsync(core, profile).ConfigureAwait(true),
                    url,
                    profile.DisplayName);
            }

            activity?.Invoke("reading the feed");

            var posts = await HarvestFeedAsync(core, profile, cancellationToken, activity).ConfigureAwait(true);

            var page = Decode(
                await ReadPageAsync(core, profile).ConfigureAwait(true),
                url,
                profile.DisplayName);

            if (page is null) return null;

            return new LoadedDocument
            {
                Url = page.Url,
                Title = page.Title,
                Text = page.Text,
                Posts = posts.Count > 0 ? posts : page.Posts,
                SourceName = profile.DisplayName
            };
        }
        finally
        {
            core.NavigationCompleted -= OnNavigationCompleted;
        }
    }

    /// <summary>Loads a URL without any scanning treatment, for the analyst's own browsing.</summary>
    public void Navigate(string url)
    {
        var core = _view.CoreWebView2;
        if (core is null) return;

        core.Navigate(url);
    }

    public void ClearSession()
    {
        var core = _view.CoreWebView2;
        if (core is null) return;

        core.CookieManager.DeleteAllCookies();
        core.Navigate("about:blank");
    }

    /// <summary>
    /// Collects posts while the feed loads more of them.
    ///
    /// A feed only keeps a handful of posts in the DOM at once: scrolling a long way and
    /// reading the page at the end finds no more than the last screenful. Instead every
    /// round reads what is currently mounted, adds anything new, scrolls, and repeats.
    /// Harvesting stops when the feed stops growing (two rounds with no new posts and no
    /// extra page height), so the scan follows an infinite scroll to its end rather than
    /// guessing at a fixed number of scrolls.
    /// </summary>
    private static async Task<List<LoadedPost>> HarvestFeedAsync(
        CoreWebView2 core,
        SourceProfile profile,
        CancellationToken cancellationToken,
        Action<string>? activity)
    {
        var collected = new List<LoadedPost>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var previousHeight = 0.0;
        var stalls = 0;

        for (var round = 0; round <= profile.ScrollPasses; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Re-probe each round: a feed that did not overflow on the first pass only
            // becomes scrollable once its first batch lands.
            await core
                .ExecuteScriptAsync(SourceProfile.BuildScrollProbeScript(profile))
                .ConfigureAwait(true);

            var posts = DecodePosts(
                await core.ExecuteScriptAsync(SourceProfile.BuildPostsScript(profile)).ConfigureAwait(true));

            var added = 0;

            foreach (var post in posts)
            {
                var key = string.IsNullOrEmpty(post.Permalink) ? post.Text : post.Permalink!;

                if (seen.Add(key))
                {
                    collected.Add(post);
                    added++;
                }
            }

            var metrics = await MeasureScrollAsync(core).ConfigureAwait(true);

            var grew = metrics.ScrollHeight > previousHeight;
            previousHeight = Math.Max(previousHeight, metrics.ScrollHeight);

            // "Nothing new" only means the end once the container is genuinely at its
            // bottom. While it can still move, keep going: a slow feed mounts the next
            // batch a moment after the scroll, and stopping here loses real posts.
            var atBottom = metrics.ScrollHeight <= 0
                           || metrics.ScrollTop + metrics.ClientHeight >= metrics.ScrollHeight - 40;

            // Only a stall at the genuine bottom of the container means the feed is done.
            // The 80% step winds back once when it hits the end, so the next advance sweeps
            // the loader again; more content there clears the count.
            if (added > 0 || grew)
            {
                stalls = 0;
            }
            else if (atBottom)
            {
                stalls++;
            }

            if (collected.Count >= profile.MaxPosts) break;
            if (stalls >= 2) break;
            if (round == profile.ScrollPasses) break;

            activity?.Invoke(
                $"scrolling feed · {collected.Count} post(s) collected, step {round + 1} of {profile.ScrollPasses}");

            await core.ExecuteScriptAsync(SourceProfile.BuildScrollStepScript()).ConfigureAwait(true);

            // Wait for the feed to actually grow rather than sleeping a fixed amount and
            // hoping. The next batch usually lands within a few hundred milliseconds, but a
            // slow connection can take seconds; polling gets both cases right.
            await WaitForFeedGrowthAsync(core, metrics.ScrollHeight, cancellationToken).ConfigureAwait(true);
        }

        return collected;
    }

    /// <summary>
    /// Waits until the feed has loaded more content, or gives up after a limit. This is
    /// what replaces a blind pause after each scroll.
    /// </summary>
    private static async Task WaitForFeedGrowthAsync(
        CoreWebView2 core,
        double previousHeight,
        CancellationToken cancellationToken)
    {
        var waited = 0;

        while (waited < FeedGrowthTimeoutMs)
        {
            await Task.Delay(FeedGrowthPollMs, cancellationToken).ConfigureAwait(true);
            waited += FeedGrowthPollMs;

            var metrics = await MeasureScrollAsync(core).ConfigureAwait(true);

            if (metrics.ScrollHeight > previousHeight + 20) return;
        }
    }

    /// <summary>How many posts are mounted right now. Cheap enough to poll.</summary>
    private static async Task<int> CountPostsAsync(CoreWebView2 core, SourceProfile profile)
    {
        var raw = await core
            .ExecuteScriptAsync(SourceProfile.BuildPostCountScript(profile))
            .ConfigureAwait(true);

        return int.TryParse(raw, out var count) ? count : 0;
    }

    /// <summary>
    /// Waits until the feed has mounted its first posts, returning as soon as they appear
    /// rather than burning a fixed delay on every page.
    /// </summary>
    private static async Task WaitForPostsAsync(
        CoreWebView2 core,
        SourceProfile profile,
        CancellationToken cancellationToken)
    {
        var waited = 0;

        while (waited < FirstPostTimeoutMs)
        {
            if (await CountPostsAsync(core, profile).ConfigureAwait(true) > 0) return;

            await Task.Delay(FirstPostPollMs, cancellationToken).ConfigureAwait(true);
            waited += FirstPostPollMs;
        }
    }

    /// <summary>
    /// Scrolls a page that has no structured posts to harvest (the ad library) so its
    /// lazy-loaded cards mount. Each step waits for the page to actually grow, so the
    /// script does not run ahead of the cards it is meant to collect.
    /// </summary>
    private static async Task ScrollToBottomAsync(
        CoreWebView2 core,
        SourceProfile profile,
        CancellationToken cancellationToken,
        Action<string>? activity)
    {
        for (var i = 0; i < profile.ScrollPasses; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var metrics = await MeasureScrollAsync(core).ConfigureAwait(true);

            await core.ExecuteScriptAsync(SourceProfile.BuildScrollStepScript()).ConfigureAwait(true);

            await WaitForFeedGrowthAsync(core, metrics.ScrollHeight, cancellationToken).ConfigureAwait(true);

            activity?.Invoke($"loading more · step {i + 1} of {profile.ScrollPasses}");
        }
    }

    private static async Task<string?> ReadPageAsync(CoreWebView2 core, SourceProfile profile)
    {
        return await core.ExecuteScriptAsync(SourceProfile.BuildExtractionScript(profile)).ConfigureAwait(true);
    }

    private static async Task<ScrollMetrics> MeasureScrollAsync(CoreWebView2 core)
    {
        var raw = await core.ExecuteScriptAsync(SourceProfile.BuildScrollMeasureScript()).ConfigureAwait(true);
        var inner = Unwrap(raw);

        if (inner is null) return new ScrollMetrics();

        try
        {
            return JsonSerializer.Deserialize<ScrollMetrics>(inner, JsonOptions) ?? new ScrollMetrics();
        }
        catch (JsonException)
        {
            return new ScrollMetrics();
        }
    }

    private static LoadedDocument? Decode(string? raw, string requestedUrl, string sourceName)
    {
        var inner = Unwrap(raw);

        if (inner is null) return null;

        PagePayload? payload;

        try
        {
            payload = JsonSerializer.Deserialize<PagePayload>(inner, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload is null) return null;

        return new LoadedDocument
        {
            Url = string.IsNullOrWhiteSpace(payload.Url) ? requestedUrl : payload.Url!,
            Title = payload.Title ?? string.Empty,
            Text = payload.Text ?? string.Empty,
            Posts = payload.Posts is null ? new List<LoadedPost>() : ToPosts(payload.Posts),
            SourceName = sourceName
        };
    }

    private static List<LoadedPost> DecodePosts(string? raw)
    {
        var inner = Unwrap(raw);

        if (inner is null) return new List<LoadedPost>();

        try
        {
            var posts = JsonSerializer.Deserialize<List<PagePost>>(inner, JsonOptions);

            return posts is null ? new List<LoadedPost>() : ToPosts(posts);
        }
        catch (JsonException)
        {
            return new List<LoadedPost>();
        }
    }

    private static List<LoadedPost> ToPosts(IEnumerable<PagePost> posts) =>
        posts
            .Where(post => !string.IsNullOrWhiteSpace(post.Text))
            .Select(post => new LoadedPost(
                post.Text!.Trim(),
                string.IsNullOrWhiteSpace(post.Permalink) ? null : post.Permalink))
            .ToList();

    /// <summary>
    /// WebView2 hands back the script's return value as a JSON string literal, so the
    /// payload is JSON encoded twice.
    /// </summary>
    private static string? Unwrap(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "null") return null;

        try
        {
            return JsonSerializer.Deserialize<string>(raw);
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    /// <summary>Position and extent of whichever element is doing the scrolling.</summary>
    private sealed class ScrollMetrics
    {
        public string Mode { get; set; } = "window";
        public double ScrollTop { get; set; }
        public double ScrollHeight { get; set; }
        public double ClientHeight { get; set; }
    }
}
