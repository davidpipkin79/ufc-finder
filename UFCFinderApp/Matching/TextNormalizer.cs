using System.Globalization;
using System.Text;

namespace UFCFinderApp.Matching;

/// <summary>
/// A searchable copy of a piece of text plus a map from every character in that copy
/// back to its position in the original text. The map lets the scanner show the analyst
/// the real, unmodified sentence around a match instead of lower-cased scraped text.
/// </summary>
public sealed class NormalizedText
{
    private readonly int[] _map;

    public NormalizedText(string text, int[] map)
    {
        Text = text;
        _map = map;
    }

    public string Text { get; }

    public int Length => Text.Length;

    /// <summary>Maps an index in <see cref="Text"/> to an index in the original text.</summary>
    public int ToOriginal(int index)
    {
        if (_map.Length == 0) return 0;
        if (index <= 0) return _map[0];
        return index >= _map.Length ? _map[^1] : _map[index];
    }
}

public static class TextNormalizer
{
    // Lower-case accented characters and their unaccented equivalent. Kept as a lookup
    // instead of a Unicode FormD round-trip so that character positions stay stable.
    private const string Accented = "áàâäãåéèêëíìîïóòôöõøúùûüñçýÿšžćčğış";
    private const string Plain    = "aaaaaaeeeeiiiioooooouuuuncyyszccgis";

    private static readonly Dictionary<char, char> AccentMap = BuildAccentMap();

    private static Dictionary<char, char> BuildAccentMap()
    {
        var map = new Dictionary<char, char>();
        var count = Math.Min(Accented.Length, Plain.Length);

        for (var i = 0; i < count; i++)
        {
            map[Accented[i]] = Plain[i];
        }

        return map;
    }

    /// <summary>
    /// Lower-cases, strips accents, unifies quote/dash characters and collapses all
    /// whitespace to single spaces, tracking where each surviving character came from.
    /// </summary>
    public static NormalizedText NormalizeWithMap(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return new NormalizedText(string.Empty, Array.Empty<int>());
        }

        var sb = new StringBuilder(input.Length);
        var map = new List<int>(input.Length);
        var lastWasSpace = true; // trims leading whitespace

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];

            if (c == '\u00A0' || c == '\u2007' || c == '\u202F')
            {
                c = ' ';
            }

            if (char.IsWhiteSpace(c))
            {
                if (lastWasSpace) continue;

                sb.Append(' ');
                map.Add(i);
                lastWasSpace = true;
                continue;
            }

            if (char.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue; // combining accent left over from a decomposed string
            }

            c = c switch
            {
                '\u2019' or '\u2018' or '\u201B' => '\'',
                '\u201C' or '\u201D' => '"',
                '\u2013' or '\u2014' or '\u2212' => '-',
                _ => c
            };

            c = char.ToLowerInvariant(c);

            if (AccentMap.TryGetValue(c, out var plain))
            {
                c = plain;
            }

            sb.Append(c);
            map.Add(i);
            lastWasSpace = false;
        }

        var length = sb.Length;

        while (length > 0 && sb[length - 1] == ' ')
        {
            length--;
        }

        if (length != sb.Length)
        {
            sb.Length = length;
            map.RemoveRange(length, map.Count - length);
        }

        return new NormalizedText(sb.ToString(), map.ToArray());
    }

    public static string Normalize(string? input) => NormalizeWithMap(input).Text;

    /// <summary>Turns any scraped snippet into a single tidy line.</summary>
    public static string ToSingleLine(string? input, int maxLength = 400)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var sb = new StringBuilder(Math.Min(input.Length, maxLength + 1));
        var lastWasSpace = false;

        foreach (var c in input)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                if (lastWasSpace) continue;

                sb.Append(' ');
                lastWasSpace = true;
                continue;
            }

            sb.Append(c);
            lastWasSpace = false;

            if (sb.Length >= maxLength) break;
        }

        var result = sb.ToString().Trim();

        return result.Length >= maxLength ? result + "…" : result;
    }
}
