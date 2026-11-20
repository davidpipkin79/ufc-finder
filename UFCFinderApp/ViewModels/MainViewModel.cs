using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using UFCFinderApp.Models;

namespace UFCFinderApp.ViewModels;

public sealed class FindingRow
{
    public string Severity { get; init; } = string.Empty;
    public string SeverityColor { get; init; } = "#9AA3AF";
    public int Score { get; init; }
    public string Business { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Matched { get; init; } = string.Empty;
    public string Signals { get; init; } = string.Empty;
    public string Evidence { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;

    public string Tooltip =>
        string.IsNullOrEmpty(Signals) ? Evidence : $"{Evidence}\n\nSignals: {Signals}";

    public static FindingRow From(ScanFinding finding) => new()
    {
        Severity = finding.Severity,
        SeverityColor = finding.Severity switch
        {
            "High" => "#FF6B5B",
            "Medium" => "#E8B33A",
            _ => "#9AA3AF"
        },
        Score = finding.Score,
        Business = finding.Business,
        Address = finding.AddressLine,
        Source = finding.Source,
        Matched = string.Join(", ", finding.MatchedPhrases),
        Signals = string.Join(", ", finding.Signals),
        Evidence = finding.Evidence,
        Url = finding.Url
    };
}

public sealed class LegalRow
{
    public string Business { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

public sealed class ErrorRow
{
    public string Url { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private string _phrases = string.Empty;
    private string _legalListPath = string.Empty;
    private string _watchListPath = string.Empty;
    private bool _useAdLibrary = true;
    private bool _useDiscovery = true;
    private bool _collectPosts = true;
    private bool _useMobileView = true;
    private double _requestDelaySeconds = 0.5;
    private bool _isBusy;
    private int _progressValue;
    private int _progressMaximum = 1;
    private string _statusText = "Ready. Choose a legal list and a watch list, then start a scan.";
    private string _sessionStatus = "Starting browser session…";
    private string _summaryText = string.Empty;
    private string _addressText = "https://www.facebook.com/";

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<FindingRow> Findings { get; } = new();
    public ObservableCollection<LegalRow> LegalRows { get; } = new();
    public ObservableCollection<ErrorRow> ErrorRows { get; } = new();

    public string Phrases
    {
        get => _phrases;
        set => Set(ref _phrases, value);
    }

    public string LegalListPath
    {
        get => _legalListPath;
        set
        {
            if (Set(ref _legalListPath, value)) Raise(nameof(LegalListFileName));
        }
    }

    public string WatchListPath
    {
        get => _watchListPath;
        set
        {
            if (Set(ref _watchListPath, value)) Raise(nameof(WatchListFileName));
        }
    }

    public string LegalListFileName => FileNameOrPlaceholder(_legalListPath);
    public string WatchListFileName => FileNameOrPlaceholder(_watchListPath);

    public bool UseAdLibrary
    {
        get => _useAdLibrary;
        set => Set(ref _useAdLibrary, value);
    }

    public bool UseDiscovery
    {
        get => _useDiscovery;
        set => Set(ref _useDiscovery, value);
    }

    public bool CollectPosts
    {
        get => _collectPosts;
        set => Set(ref _collectPosts, value);
    }

    /// <summary>Scan Facebook in the mobile layout, which exposes more of a long feed.</summary>
    public bool UseMobileView
    {
        get => _useMobileView;
        set => Set(ref _useMobileView, value);
    }

    public double RequestDelaySeconds
    {
        get => _requestDelaySeconds;
        set
        {
            if (Set(ref _requestDelaySeconds, value)) Raise(nameof(RequestDelayLabel));
        }
    }

    public string RequestDelayLabel => $"{_requestDelaySeconds:0.0}s";

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (Set(ref _isBusy, value)) Raise(nameof(IsIdle));
        }
    }

    public bool IsIdle => !_isBusy;

    public int ProgressValue
    {
        get => _progressValue;
        set => Set(ref _progressValue, value);
    }

    public int ProgressMaximum
    {
        get => _progressMaximum;
        set => Set(ref _progressMaximum, value < 1 ? 1 : value);
    }

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    public string SessionStatus
    {
        get => _sessionStatus;
        set => Set(ref _sessionStatus, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        set => Set(ref _summaryText, value);
    }

    public string AddressText
    {
        get => _addressText;
        set => Set(ref _addressText, value);
    }

    public void Apply(ScanReport report)
    {
        Findings.Clear();
        LegalRows.Clear();
        ErrorRows.Clear();

        foreach (var finding in report.Findings)
        {
            Findings.Add(FindingRow.From(finding));
        }

        foreach (var legal in report.LegalMatches)
        {
            LegalRows.Add(new LegalRow
            {
                Business = legal.Business,
                Address = legal.AddressLine,
                Reason = legal.Reason
            });
        }

        foreach (var error in report.Errors)
        {
            ErrorRows.Add(new ErrorRow { Url = error.Url, Message = error.Message });
        }

        SummaryText =
            $"{report.Findings.Count} lead(s) · {report.PagesScanned} page(s) · "
            + $"{report.PostsScanned} item(s) read · {report.LegalMatches.Count} filtered · "
            + $"{report.Errors.Count} not scanned";
    }

    /// <summary>Adds a lead as it is found, keeping the grid ranked by score.</summary>
    public void AddFinding(ScanFinding finding)
    {
        var row = FindingRow.From(finding);
        var index = 0;

        while (index < Findings.Count && Findings[index].Score >= row.Score) index++;

        Findings.Insert(index, row);
    }

    public void AddLegalMatch(LegalMatch match) => LegalRows.Add(new LegalRow
    {
        Business = match.Business,
        Address = match.AddressLine,
        Reason = match.Reason
    });

    public void AddError(ScanError error) => ErrorRows.Add(new ErrorRow
    {
        Url = error.Url,
        Message = error.Message
    });

    public void UpdateLiveSummary() =>
        SummaryText = $"{Findings.Count} lead(s) · {LegalRows.Count} filtered · {ErrorRows.Count} not scanned";

    public void ClearResults()
    {
        Findings.Clear();
        LegalRows.Clear();
        ErrorRows.Clear();
        SummaryText = string.Empty;
    }

    private static string FileNameOrPlaceholder(string path) =>
        string.IsNullOrWhiteSpace(path) ? "No file selected" : Path.GetFileName(path);

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;

        field = value;
        Raise(propertyName);
        return true;
    }

    private void Raise(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
