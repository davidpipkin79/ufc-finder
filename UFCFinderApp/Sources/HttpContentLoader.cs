using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace UFCFinderApp.Sources;

/// <summary>
/// Loads pages over plain HTTP. Using the network stack instead of a browser for
/// public pages is far faster and lets the browser stay free for the sites that
/// actually require a signed-in session.
/// </summary>
public sealed class HttpContentLoader : IDisposable
{
    private readonly HttpClient _http;

    public HttpContentLoader()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 6
        };

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(40) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    public HttpClient Client => _http;

    public async Task<LoadedDocument?> LoadAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(true);

        if (!response.IsSuccessStatusCode) return null;

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

        if (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
            && !mediaType.Contains("text", StringComparison.OrdinalIgnoreCase)
            && mediaType.Length > 0)
        {
            return null;
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
        var extract = HtmlTextExtractor.Extract(html);

        return new LoadedDocument
        {
            Url = response.RequestMessage?.RequestUri?.ToString() ?? url,
            Title = extract.Title,
            Text = extract.Text,
            Posts = Array.Empty<LoadedPost>(),
            SourceName = SourceProfile.LabelFor(url)
        };
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Dependency-free HTML to text conversion. It is intentionally conservative: strip the
/// non-visible parts of the document, turn block boundaries into newlines, decode
/// entities, then collapse runs of whitespace.
/// </summary>
public static class HtmlTextExtractor
{
    private static readonly Regex NonVisible = new(
        @"<(script|style|noscript|svg|template|iframe)\b[^>]*>.*?</\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex Comments = new(
        @"<!--.*?-->",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TitleTag = new(
        @"<title[^>]*>(?<title>.*?)</title>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex BlockBreaks = new(
        @"<\s*(br|/p|/div|/li|/tr|/h[1-6]|/section|/article)\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.Compiled);

    private static readonly Regex Spaces = new(@"[ \t\r\f\v\u00A0]+", RegexOptions.Compiled);
    private static readonly Regex BlankLines = new(@"\n\s*\n\s*\n+", RegexOptions.Compiled);

    private const int MaxTextLength = 400_000;

    public static (string Title, string Text) Extract(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return (string.Empty, string.Empty);

        var title = string.Empty;
        var titleMatch = TitleTag.Match(html);

        if (titleMatch.Success)
        {
            title = WebUtility.HtmlDecode(Tags.Replace(titleMatch.Groups["title"].Value, string.Empty)).Trim();
        }

        var working = Comments.Replace(html, " ");
        working = NonVisible.Replace(working, " ");
        working = BlockBreaks.Replace(working, "\n");
        working = Tags.Replace(working, " ");

        var text = WebUtility.HtmlDecode(working);
        text = Spaces.Replace(text, " ");
        text = BlankLines.Replace(text, "\n\n").Trim();

        if (text.Length > MaxTextLength)
        {
            text = text[..MaxTextLength];
        }

        return (title, text);
    }
}
