namespace UFCFinderApp.Matching;

/// <summary>An excerpt chosen to show a human where an event was mentioned.</summary>
public sealed record EvidenceWindow(string Excerpt, string NormalizedText);

/// <summary>
/// Chooses the most useful excerpt on a page rather than simply the first hit.
///
/// Large pages name the event in the title and navigation before they ever discuss it,
/// so "first match" quotes a menu instead of the sentence that matters. Here every
/// occurrence competes: the window containing the most distinct phrases, and the most
/// commercial signals, wins.
/// </summary>
public static class EvidenceSelector
{
    private const int Before = 150;
    private const int After = 250;

    public static EvidenceWindow Select(
        string original,
        NormalizedText normalized,
        IReadOnlyList<PhraseMatch> matches)
    {
        if (matches.Count == 0 || normalized.Length == 0)
        {
            return new EvidenceWindow(string.Empty, string.Empty);
        }

        var ordered = matches.OrderBy(match => match.Index).ToList();
        var best = ordered[0];
        var bestScore = double.MinValue;

        for (var i = 0; i < ordered.Count; i++)
        {
            var anchor = ordered[i].Index;
            var from = Math.Max(0, anchor - Before);
            var to = Math.Min(normalized.Length, anchor + After);

            // Count DISTINCT phrases, not raw hits: a reference list that repeats the
            // event name twenty times is less informative than the sentence that also
            // talks about how the event was shown.
            var distinctPhrases = ordered
                .Where(match => match.Index >= from && match.Index < to)
                .Select(match => match.Phrase)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            var signals = FindingScorer.CountSignals(normalized.Text[from..to]);

            // Prefer windows with more distinct phrases and more commercial signals; the
            // small position penalty keeps the earliest equally-good window on ties.
            var score = distinctPhrases * 10.0 + signals * 4.0 - i * 0.01;

            if (score <= bestScore) continue;

            bestScore = score;
            best = ordered[i];
        }

        var startNormalized = Math.Max(0, best.Index - Before);
        var endNormalized = Math.Min(normalized.Length, best.Index + After);

        var startOriginal = Math.Max(0, normalized.ToOriginal(startNormalized));
        var lastOriginal = normalized.ToOriginal(Math.Max(0, endNormalized - 1));
        var endOriginal = Math.Min(original.Length, lastOriginal + 1);

        if (endOriginal <= startOriginal)
        {
            return new EvidenceWindow(string.Empty, string.Empty);
        }

        var excerpt = TextNormalizer.ToSingleLine(original[startOriginal..endOriginal], 340);

        if (startOriginal > 0)
        {
            excerpt = "…" + excerpt;
        }

        return new EvidenceWindow(excerpt, normalized.Text[startNormalized..endNormalized]);
    }
}
