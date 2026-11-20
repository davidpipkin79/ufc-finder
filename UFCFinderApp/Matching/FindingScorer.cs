using System.Text.RegularExpressions;

namespace UFCFinderApp.Matching;

public sealed record ScoreResult(int Score, IReadOnlyList<string> Reasons)
{
    public string Severity => Score >= 60 ? "High" : Score >= 30 ? "Medium" : "Low";
}

/// <summary>
/// Turns a set of phrase hits into a ranked lead instead of a yes/no.
/// A post that says "UFC 320 watch party, $20 cover, doors at 9" is a much stronger
/// lead than a post that only mentions the event name.
/// </summary>
public static class FindingScorer
{
    private static readonly Regex CommercialIntent = new(
        @"\b(watch[\s-]*party|viewing[\s-]*party|ppv|pay[\s-]*per[\s-]*view|cover[\s-]*charge|cover|admission|tickets?|"
        + @"streams?|streaming|livestreams?|link[\s-]*in[\s-]*bio|dm\s*(?:me|us|for)|inbox\s*me|cash[\s-]*only|"
        + @"doors?\s*open|screening|broadcast|satellite|decoder|iptv|fire[\s-]*stick|firestick|password|login|feed)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PriceSignal = new(
        @"\$\s?\d{1,4}|\b\d{1,3}\s*(?:dollars|bucks|usd)\b|\b(?:free|no)\s*cover\b|\bper\s*person\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TimingSignal = new(
        @"\b(tonight|today|tomorrow|this\s+(?:sat|saturday|sun|sunday|weekend|evening)|"
        + @"saturday|sunday|doors?\s+(?:open|at)|main\s*card|prelims?|early\s*prelims?|"
        + @"\d{1,2}(?::\d{2})?\s*(?:am|pm))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ChannelSignal = new(
        @"\b(telegram|whatsapp|discord|signal\s*app|snapchat|venmo|cashapp|cash\s*app|zelle|reserve|rsvp)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const int BasePointsPerPhrase = 12;
    private const int CommercialIntentPoints = 18;
    private const int PricePoints = 12;
    private const int TimingPoints = 10;
    private const int ChannelPoints = 8;

    public static ScoreResult Score(string normalizedText, IReadOnlyList<PhraseMatch> matches)
    {
        var score = 0;
        var reasons = new List<string>();

        // Repeated occurrences of the same phrase must not multiply the score.
        var distinct = matches
            .GroupBy(match => match.Phrase, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First());

        foreach (var match in distinct)
        {
            score += BasePointsPerPhrase * match.Weight;
        }

        if (CommercialIntent.IsMatch(normalizedText))
        {
            score += CommercialIntentPoints;
            reasons.Add("commercial intent");
        }

        if (PriceSignal.IsMatch(normalizedText))
        {
            score += PricePoints;
            reasons.Add("price or cover charge");
        }

        if (TimingSignal.IsMatch(normalizedText))
        {
            score += TimingPoints;
            reasons.Add("event timing");
        }

        if (ChannelSignal.IsMatch(normalizedText))
        {
            score += ChannelPoints;
            reasons.Add("link or contact sharing");
        }

        return new ScoreResult(Math.Min(score, 100), reasons);
    }

    /// <summary>How many distinct commercial signals appear in a snippet (0-4).</summary>
    public static int CountSignals(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var count = 0;

        if (CommercialIntent.IsMatch(text)) count++;
        if (PriceSignal.IsMatch(text)) count++;
        if (TimingSignal.IsMatch(text)) count++;
        if (ChannelSignal.IsMatch(text)) count++;

        return count;
    }
}
