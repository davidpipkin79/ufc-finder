namespace UFCFinderApp.Sources;

/// <summary>A single block of content lifted from a page: one post when the source exposes
/// posts, otherwise the whole page.</summary>
public sealed record LoadedPost(string Text, string? Permalink);

/// <summary>What a loader hands back after visiting a URL.</summary>
public sealed class LoadedDocument
{
    public string Url { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public IReadOnlyList<LoadedPost> Posts { get; init; } = Array.Empty<LoadedPost>();
    public string SourceName { get; init; } = string.Empty;

    public bool HasContent => Text.Length > 0 || Posts.Count > 0;
}
