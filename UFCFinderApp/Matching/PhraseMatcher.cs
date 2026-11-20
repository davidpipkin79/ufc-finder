using System.Text.RegularExpressions;

namespace UFCFinderApp.Matching;

public sealed record PhraseMatch(string Phrase, int Index, int Length, int Weight);

/// <summary>
/// Compiles the analyst's search phrases into whole-word regular expressions.
/// Whole-word matching matters here: the old substring check flagged "ufc 300" inside
/// "ufc 3000" and "UFC 5" inside a phone number, which buried real leads in noise.
/// </summary>
public sealed class PhraseMatcher
{
    private static readonly Regex HighIntentPhrase = new(
        @"\b(ppv|pay[\s-]*per[\s-]*view|watch[\s-]*party|viewing[\s-]*party|cover|cover[\s-]*charge|admission|tickets?|"
        + @"stream|streaming|livestream|screening|broadcast|showing|showings?|buy[\s-]*in|door|entry|iptv|feed)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly List<CompiledPhrase> _phrases;

    public PhraseMatcher(IEnumerable<string?> phrases)
    {
        _phrases = phrases
            .Select(p => (p ?? string.Empty).Trim())
            .Where(p => p.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => new CompiledPhrase(p, WeightFor(p), BuildRegex(p)))
            .ToList();
    }

    public int Count => _phrases.Count;

    public IReadOnlyList<string> Phrases => _phrases.Select(p => p.Phrase).ToList();

    /// <summary>
    /// Returns every occurrence of every phrase (capped per phrase). Scoring uses the
    /// distinct phrases, while evidence selection uses the positions to find the most
    /// informative part of a long page.
    /// </summary>
    public List<PhraseMatch> FindAll(NormalizedText text, int maxPerPhrase = 40)
    {
        var results = new List<PhraseMatch>(_phrases.Count);

        if (text.Length == 0) return results;

        foreach (var phrase in _phrases)
        {
            var taken = 0;

            foreach (Match match in phrase.Regex.Matches(text.Text))
            {
                results.Add(new PhraseMatch(phrase.Phrase, match.Index, match.Length, phrase.Weight));

                if (++taken >= maxPerPhrase) break;
            }
        }

        return results;
    }

    /// <summary>
    /// Phrases that already describe a way of showing an event commercially carry more
    /// weight than a bare event name, which people also post about innocently.
    /// </summary>
    public static int WeightFor(string phrase) => HighIntentPhrase.IsMatch(phrase) ? 2 : 1;

    private static Regex BuildRegex(string phrase)
    {
        var normalized = TextNormalizer.Normalize(phrase);
        var body = Regex.Escape(normalized);

        // Regex.Escape turns spaces into "\ ", so collapse either form into "\s+"
        // to make matching independent of line breaks and double spacing.
        body = Regex.Replace(body, @"(?:\\\s|\s)+", @"\s+");

        return new Regex(
            $@"(?<!\w){body}(?!\w)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    private sealed record CompiledPhrase(string Phrase, int Weight, Regex Regex);
}
