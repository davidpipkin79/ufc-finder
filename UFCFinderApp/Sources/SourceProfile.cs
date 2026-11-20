using System.Text.Json;

namespace UFCFinderApp.Sources;

/// <summary>
/// Describes how a particular kind of site is visited and mined. Facebook, Instagram and
/// the ad library expose their posts through different DOM shapes, so each gets its own
/// selectors and timing; everything else falls back to the generic profile.
///
/// If a site changes its markup, this is the only place that needs updating.
/// </summary>
public sealed class SourceProfile
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>CSS selector for one post/ad card. Null means "treat the page as one post".</summary>
    public string? PostSelector { get; init; }

    /// <summary>Selector (inside a post) for that post's permalink.</summary>
    public string? PermalinkSelector { get; init; }

    /// <summary>How long to let a page finish painting before reading it.</summary>
    public int SettleDelayMs { get; init; } = 2000;

    /// <summary>How many times to scroll down to trigger lazy-loaded posts.</summary>
    public int ScrollPasses { get; init; }

    public int MaxPosts { get; init; } = 40;

    /// <summary>True when the site needs the logged-in browser session instead of plain HTTP.</summary>
    public bool RequiresBrowser { get; init; }

    public static readonly SourceProfile Generic = new()
    {
        Key = "web",
        DisplayName = "Web page",
        SettleDelayMs = 1500
    };

    public static readonly SourceProfile Facebook = new()
    {
        Key = "facebook",
        DisplayName = "Facebook",
        PostSelector = "div[role=\"article\"]",
        PermalinkSelector = "a[href*=\"/posts/\"],a[href*=\"story_fbid\"],a[href*=\"/permalink/\"],a[href*=\"/reel/\"]",
        SettleDelayMs = 3200,
        ScrollPasses = 10,
        MaxPosts = 150,
        RequiresBrowser = true
    };

    public static readonly SourceProfile Instagram = new()
    {
        Key = "instagram",
        DisplayName = "Instagram",
        PostSelector = "article",
        PermalinkSelector = "a[href*=\"/p/\"],a[href*=\"/reel/\"]",
        SettleDelayMs = 3200,
        ScrollPasses = 8,
        MaxPosts = 120,
        RequiresBrowser = true
    };

    public static readonly SourceProfile AdLibrary = new()
    {
        Key = "adlibrary",
        DisplayName = "Meta Ad Library",
        PostSelector = null,
        PermalinkSelector = null,
        SettleDelayMs = 4000,
        ScrollPasses = 10,
        RequiresBrowser = true
    };

    public static readonly SourceProfile Discovery = new()
    {
        Key = "search",
        DisplayName = "Web search",
        SettleDelayMs = 1000
    };

    public static SourceProfile For(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();

        if (host.EndsWith("facebook.com", StringComparison.Ordinal) || host.EndsWith("fb.com", StringComparison.Ordinal))
        {
            return uri.AbsolutePath.Contains("/ads/library", StringComparison.OrdinalIgnoreCase)
                ? AdLibrary
                : Facebook;
        }

        if (host.EndsWith("instagram.com", StringComparison.Ordinal) || host.EndsWith("instagr.am", StringComparison.Ordinal))
        {
            return Instagram;
        }

        return Generic;
    }

    public static string LabelFor(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? LabelFor(uri) : "Web page";

    /// <summary>The label shown in the results grid. More specific than <see cref="DisplayName"/>.</summary>
    public static string LabelFor(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath;

        if (host.EndsWith("facebook.com", StringComparison.Ordinal) || host.EndsWith("fb.com", StringComparison.Ordinal))
        {
            if (path.Contains("/ads/library", StringComparison.OrdinalIgnoreCase)) return "Meta Ad Library";
            if (path.Contains("/groups/", StringComparison.OrdinalIgnoreCase)) return "Facebook group";
            if (path.Contains("/events/", StringComparison.OrdinalIgnoreCase)) return "Facebook event";
            return "Facebook page";
        }

        if (host.EndsWith("instagram.com", StringComparison.Ordinal) || host.EndsWith("instagr.am", StringComparison.Ordinal))
        {
            return "Instagram";
        }

        if (host.EndsWith("tiktok.com", StringComparison.Ordinal)) return "TikTok";
        if (host.EndsWith("x.com", StringComparison.Ordinal) || host.EndsWith("twitter.com", StringComparison.Ordinal)) return "X";
        if (path.Contains("search", StringComparison.OrdinalIgnoreCase)) return "Web search";

        return "Web page";
    }

    /// <summary>
    /// Finds the element that actually holds the feed and remembers it on the page.
    ///
    /// Plenty of feeds are not scrolled by the window: Facebook, the ad library and most
    /// single-page apps put the list inside an overflow container with its own scrollbar.
    /// Calling window.scrollTo on such a page does nothing, which is exactly how a harvest
    /// stops after one screenful while reporting success.
    /// </summary>
    public static string BuildScrollProbeScript(SourceProfile profile)
    {
        var postSelector = JsonSerializer.Serialize(profile.PostSelector ?? string.Empty);

        return $$"""
            (function () {
              try {
                var state = window.__ufcScan || (window.__ufcScan = {});
                var postSel = {{postSelector}};
                var posts = postSel ? document.querySelectorAll(postSel) : [];
                var anchor = posts.length ? posts[0] : null;

                function scrollable(el) {
                  if (!el || el === document.body || el === document.documentElement) { return false; }
                  var style = window.getComputedStyle(el);
                  var overflow = style.overflowY;
                  if (overflow !== 'auto' && overflow !== 'scroll' && overflow !== 'overlay') { return false; }
                  return (el.scrollHeight - el.clientHeight) > 120;
                }

                function fromPosts() {
                  var node = anchor ? anchor.parentElement : null;
                  while (node && node !== document.body) {
                    if (scrollable(node)) { return node; }
                    node = node.parentElement;
                  }
                  return null;
                }

                function widest() {
                  var candidates = document.querySelectorAll('[role=\"main\"], main, section, div');
                  var limit = Math.min(candidates.length, 1500);
                  var best = null;
                  var bestScore = 0;

                  for (var i = 0; i < limit; i++) {
                    var el = candidates[i];
                    if (!scrollable(el)) { continue; }

                    var score = el.scrollHeight - el.clientHeight;

                    // Strongly prefer the container that actually holds posts.
                    if (anchor && el.contains(anchor)) { score += 100000; }

                    if (score > bestScore) { bestScore = score; best = el; }
                  }

                  return best;
                }

                var scroller = fromPosts() || widest();
                state.scroller = scroller;

                var target = scroller || document.documentElement;

                return JSON.stringify({
                  mode: scroller ? 'element' : 'window',
                  tag: scroller ? scroller.tagName.toLowerCase() : 'document',
                  scrollTop: scroller ? scroller.scrollTop : window.scrollY,
                  scrollHeight: target.scrollHeight,
                  clientHeight: scroller ? scroller.clientHeight : window.innerHeight
                });
              } catch (e) {
                return JSON.stringify({ mode: 'window', error: String(e) });
              }
            })()
            """;
    }

    /// <summary>Moves the probed feed container (and the window, when it also scrolls).</summary>
    public static string BuildScrollStepScript() => """
        (function () {
          try {
            var state = window.__ufcScan;
            var el = state ? state.scroller : null;

            if (el && !document.contains(el)) {
              el = null;
              if (state) { state.scroller = null; }
            }

            var step = Math.max(window.innerHeight || 800, 700);

            if (el) {
              el.scrollTop = el.scrollTop + Math.max(el.clientHeight, step) * 1.5;

              if (document.documentElement.scrollHeight > window.innerHeight + 300) {
                window.scrollBy(0, step);
              }

              return 'element';
            }

            window.scrollTo(0, Math.max(document.documentElement.scrollHeight, document.body ? document.body.scrollHeight : 0));

            return 'window';
          } catch (e) {
            return 'error';
          }
        })()
        """;

    /// <summary>Position and extent of the feed container, used to detect the real end.</summary>
    public static string BuildScrollMeasureScript() => """
        (function () {
          try {
            var state = window.__ufcScan;
            var el = state ? state.scroller : null;

            if (el && document.contains(el)) {
              return JSON.stringify({
                mode: 'element',
                scrollTop: el.scrollTop,
                scrollHeight: el.scrollHeight,
                clientHeight: el.clientHeight
              });
            }

            return JSON.stringify({
              mode: 'window',
              scrollTop: window.scrollY,
              scrollHeight: document.documentElement.scrollHeight,
              clientHeight: window.innerHeight
            });
          } catch (e) {
            return JSON.stringify({ mode: 'window', scrollTop: 0, scrollHeight: 0, clientHeight: 0 });
          }
        })()
        """;

    /// <summary>
    /// Posts only. Used repeatedly while a feed scrolls so that each round can add what
    /// is newly mounted without transferring the whole document every time.
    /// </summary>
    public static string BuildPostsScript(SourceProfile profile)
    {
        var postSelector = JsonSerializer.Serialize(profile.PostSelector ?? string.Empty);
        var linkSelector = JsonSerializer.Serialize(profile.PermalinkSelector ?? string.Empty);
        var maxPosts = profile.MaxPosts;

        return $$"""
            (function () {
              try {
                var postSel = {{postSelector}};
                var linkSel = {{linkSelector}};
                var maxPosts = {{maxPosts}};
                var nodes = postSel ? Array.prototype.slice.call(document.querySelectorAll(postSel)) : [];
                var posts = [];

                for (var i = 0; i < nodes.length && posts.length < maxPosts; i++) {
                  var node = nodes[i];
                  var text = (node.innerText || node.textContent || '').replace(/\s+/g, ' ').trim();
                  if (text.length < 20) { continue; }
                  var link = linkSel ? node.querySelector(linkSel) : null;
                  posts.push({ permalink: link ? link.href : '', text: text });
                }

                return JSON.stringify(posts);
              } catch (e) {
                return '[]';
              }
            })()
            """;
    }

    /// <summary>
    /// Builds the script that pulls posts out of a live page. Returning structured posts
    /// (text plus permalink) instead of one giant body blob is what lets the scanner point
    /// at the exact post that matched and ignore surrounding navigation and comment noise.
    /// </summary>
    public static string BuildExtractionScript(SourceProfile profile)
    {
        var postSelector = JsonSerializer.Serialize(profile.PostSelector ?? string.Empty);
        var linkSelector = JsonSerializer.Serialize(profile.PermalinkSelector ?? string.Empty);
        var maxPosts = profile.MaxPosts;

        return $$"""
            (function () {
              try {
                var postSel = {{postSelector}};
                var linkSel = {{linkSelector}};
                var maxPosts = {{maxPosts}};
                var nodes = postSel ? Array.prototype.slice.call(document.querySelectorAll(postSel)) : [];
                var posts = [];

                for (var i = 0; i < nodes.length && posts.length < maxPosts; i++) {
                  var node = nodes[i];
                  var text = (node.innerText || node.textContent || '').replace(/\s+/g, ' ').trim();
                  if (text.length < 20) { continue; }
                  var link = linkSel ? node.querySelector(linkSel) : null;
                  posts.push({ permalink: link ? link.href : '', text: text });
                }

                var body = document.body ? (document.body.innerText || '') : '';

                return JSON.stringify({
                  title: document.title || '',
                  url: location.href,
                  text: body.replace(/\s+/g, ' ').trim(),
                  posts: posts
                });
              } catch (e) {
                return JSON.stringify({ title: '', url: location.href, text: '', posts: [], error: String(e) });
              }
            })()
            """;
    }
}

/// <summary>Shape of the JSON produced by <see cref="SourceProfile.BuildExtractionScript"/>.</summary>
internal sealed class PagePayload
{
    public string? Title { get; set; }
    public string? Url { get; set; }
    public string? Text { get; set; }
    public string? Error { get; set; }
    public List<PagePost>? Posts { get; set; }
}

internal sealed class PagePost
{
    public string? Permalink { get; set; }
    public string? Text { get; set; }
}
