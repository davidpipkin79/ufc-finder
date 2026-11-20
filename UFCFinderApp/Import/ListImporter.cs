using System.Text;
using ClosedXML.Excel;
using UFCFinderApp.Models;

namespace UFCFinderApp.Import;

/// <summary>
/// Reads the two analyst workbooks.
///
/// The layout is deliberately unchanged from the original tool so existing sheets keep
/// working: the legal list starts at row 3, the watch list at row 2, business/address/
/// city/state/zip in columns 1-5, and watch-list URLs in column 7 (one per line).
/// CSV files with the same layout are also accepted.
/// </summary>
public static class ListImporter
{
    private const int MaxRows = 50_000;

    public static List<LegalVenue> LoadLegalVenues(string path)
    {
        var rows = ReadRows(path);
        var venues = new List<LegalVenue>();

        // The legal list has two header rows.
        for (var i = 2; i < rows.Count; i++)
        {
            var business = Field(rows[i], 0);

            if (business.Length == 0) break;

            venues.Add(new LegalVenue
            {
                Business = business,
                Address = Field(rows[i], 1),
                City = Field(rows[i], 2),
                State = Field(rows[i], 3),
                Zip = Field(rows[i], 4)
            });
        }

        if (venues.Count == 0)
        {
            throw new FormatException(
                "No rows were found in the legal list. Data is expected from row 3, starting in column A.");
        }

        return venues;
    }

    public static List<WatchTarget> LoadWatchTargets(string path)
    {
        var rows = ReadRows(path);
        var targets = new List<WatchTarget>();

        // The watch list has one header row.
        for (var i = 1; i < rows.Count; i++)
        {
            var business = Field(rows[i], 0);

            if (business.Length == 0) break;

            var urls = Field(rows[i], 6)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(url => url.Length > 0)
                .ToList();

            // Some sheets separate URLs with commas or spaces instead of line breaks.
            if (urls.Count == 1 && urls[0].Contains(','))
            {
                urls = urls[0]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(url => url.Length > 0)
                    .ToList();
            }

            targets.Add(new WatchTarget
            {
                Business = business,
                Address = Field(rows[i], 1),
                City = Field(rows[i], 2),
                State = Field(rows[i], 3),
                Zip = Field(rows[i], 4),
                Urls = urls
            });
        }

        if (targets.Count == 0)
        {
            throw new FormatException(
                "No rows were found in the watch list. Data is expected from row 2, starting in column A.");
        }

        return targets;
    }

    private static string Field(string[] row, int index) =>
        index < row.Length ? row[index].Trim() : string.Empty;

    private static List<string[]> ReadRows(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The selected list no longer exists.", path);
        }

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".csv" or ".tsv" or ".txt" => ParseDelimited(File.ReadAllText(path)),
            _ => ReadWorkbook(path)
        };
    }

    private static List<string[]> ReadWorkbook(string path)
    {
        var rows = new List<string[]>();

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheets.FirstOrDefault()
                    ?? throw new FormatException("The workbook has no worksheets.");

        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
        var lastColumn = Math.Max(sheet.LastColumnUsed()?.ColumnNumber() ?? 0, 7);
        var rowLimit = Math.Min(lastRow, MaxRows);

        for (var row = 1; row <= rowLimit; row++)
        {
            var values = new string[lastColumn];

            for (var column = 1; column <= lastColumn; column++)
            {
                values[column - 1] = ReadCell(sheet, row, column);
            }

            rows.Add(values);
        }

        return rows;
    }

    private static string ReadCell(IXLWorksheet sheet, int row, int column)
    {
        var cell = sheet.Cell(row, column);

        if (cell.IsEmpty()) return string.Empty;

        try
        {
            return cell.GetFormattedString().Trim();
        }
        catch
        {
            return cell.GetString().Trim();
        }
    }

    /// <summary>RFC 4180 parsing, including quoted fields that span several lines.</summary>
    private static List<string[]> ParseDelimited(string text)
    {
        var rows = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;

                case ',':
                case '\t':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;

                case '\r':
                    break;

                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    rows.Add(fields.ToArray());
                    fields.Clear();
                    break;

                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            rows.Add(fields.ToArray());
        }

        return rows;
    }
}
