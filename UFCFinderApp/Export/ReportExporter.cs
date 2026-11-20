using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using UFCFinderApp.Models;

namespace UFCFinderApp.Export;

/// <summary>Writes a scan out in the formats an analyst is likely to hand to someone else.</summary>
public static class ReportExporter
{
    private static readonly string[] Headers =
    {
        "Severity", "Score", "Business", "Address", "Source", "Matched Phrases",
        "Signals", "Evidence", "URL", "Found by discovery"
    };

    public static void SaveCsv(ScanReport report, string path)
    {
        var sb = new StringBuilder();

        sb.AppendLine(string.Join(",", Headers.Select(Escape)));

        foreach (var finding in report.Findings)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                finding.Severity,
                finding.Score.ToString(),
                finding.Business,
                finding.AddressLine,
                finding.Source,
                string.Join(" | ", finding.MatchedPhrases),
                string.Join(" | ", finding.Signals),
                finding.Evidence,
                finding.Url,
                finding.Discovered ? "yes" : "no"
            }.Select(Escape)));
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    public static void SaveJson(ScanReport report, string path)
    {
        var payload = new
        {
            generatedAt = report.CompletedAt,
            startedAt = report.StartedAt,
            durationSeconds = Math.Round(report.Duration.TotalSeconds, 1),
            pagesScanned = report.PagesScanned,
            postsScanned = report.PostsScanned,
            urlsConsidered = report.UrlsConsidered,
            findings = report.Findings.Select(f => new
            {
                severity = f.Severity,
                score = f.Score,
                business = f.Business,
                address = f.AddressLine,
                source = f.Source,
                url = f.Url,
                matchedPhrases = f.MatchedPhrases,
                signals = f.Signals,
                evidence = f.Evidence,
                discovered = f.Discovered
            }),
            filteredAsLegal = report.LegalMatches.Select(l => new
            {
                business = l.Business,
                address = l.AddressLine,
                reason = l.Reason
            }),
            errors = report.Errors.Select(e => new { url = e.Url, message = e.Message })
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    public static void SaveXlsx(ScanReport report, string path)
    {
        using var workbook = new XLWorkbook();

        var findings = workbook.AddWorksheet("Findings");

        for (var column = 0; column < Headers.Length; column++)
        {
            findings.Cell(1, column + 1).Value = Headers[column];
        }

        var header = findings.Range(1, 1, 1, Headers.Length);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#1D2026");
        header.Style.Font.FontColor = XLColor.White;

        for (var i = 0; i < report.Findings.Count; i++)
        {
            var finding = report.Findings[i];
            var row = i + 2;

            findings.Cell(row, 1).Value = finding.Severity;
            findings.Cell(row, 2).Value = finding.Score;
            findings.Cell(row, 3).Value = finding.Business;
            findings.Cell(row, 4).Value = finding.AddressLine;
            findings.Cell(row, 5).Value = finding.Source;
            findings.Cell(row, 6).Value = string.Join(" | ", finding.MatchedPhrases);
            findings.Cell(row, 7).Value = string.Join(" | ", finding.Signals);
            findings.Cell(row, 8).Value = finding.Evidence;
            findings.Cell(row, 9).Value = finding.Url;
            findings.Cell(row, 10).Value = finding.Discovered ? "yes" : "no";
        }

        findings.Column(1).Width = 10;
        findings.Column(2).Width = 8;
        findings.Column(3).Width = 34;
        findings.Column(4).Width = 40;
        findings.Column(5).Width = 18;
        findings.Column(6).Width = 34;
        findings.Column(7).Width = 26;
        findings.Column(8).Width = 80;
        findings.Column(9).Width = 52;
        findings.Column(10).Width = 18;

        findings.SheetView.FreezeRows(1);
        findings.RangeUsed()?.SetAutoFilter();

        var legal = workbook.AddWorksheet("Filtered as legal");

        legal.Cell(1, 1).Value = "Business";
        legal.Cell(1, 2).Value = "Address";
        legal.Cell(1, 3).Value = "Reason";
        legal.Range(1, 1, 1, 3).Style.Font.Bold = true;

        for (var i = 0; i < report.LegalMatches.Count; i++)
        {
            legal.Cell(i + 2, 1).Value = report.LegalMatches[i].Business;
            legal.Cell(i + 2, 2).Value = report.LegalMatches[i].AddressLine;
            legal.Cell(i + 2, 3).Value = report.LegalMatches[i].Reason;
        }

        legal.Column(1).Width = 40;
        legal.Column(2).Width = 55;
        legal.Column(3).Width = 26;

        var errors = workbook.AddWorksheet("Not scanned");

        errors.Cell(1, 1).Value = "URL";
        errors.Cell(1, 2).Value = "Reason";
        errors.Range(1, 1, 1, 2).Style.Font.Bold = true;

        for (var i = 0; i < report.Errors.Count; i++)
        {
            errors.Cell(i + 2, 1).Value = report.Errors[i].Url;
            errors.Cell(i + 2, 2).Value = report.Errors[i].Message;
        }

        errors.Column(1).Width = 70;
        errors.Column(2).Width = 80;

        workbook.SaveAs(path);
    }

    private static string Escape(string? value)
    {
        var text = value ?? string.Empty;

        if (text.Contains('"') || text.Contains(',') || text.Contains('\n') || text.Contains('\r'))
        {
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        return text;
    }
}
