using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using UFCFinderApp.Export;
using UFCFinderApp.Import;
using UFCFinderApp.Models;
using UFCFinderApp.Services;
using UFCFinderApp.Sources;
using UFCFinderApp.ViewModels;

namespace UFCFinderApp;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private readonly HttpContentLoader _http = new();
    private readonly BrowserContentLoader _browser;

    private readonly List<ScanFinding> _liveFindings = new();
    private readonly List<LegalMatch> _liveLegal = new();
    private readonly List<ScanError> _liveErrors = new();

    private CancellationTokenSource? _cancellation;
    private ScanReport? _lastReport;
    private DateTimeOffset _scanStartedAt;
    private bool _browserEventsWired;

    public MainWindow()
    {
        InitializeComponent();

        DataContext = _viewModel;
        _browser = new BrowserContentLoader(Browser);

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await EnsureBrowserAsync();

        _browser.Navigate(FacebookHomeUrl);
    }

    private string FacebookHomeUrl =>
        _viewModel.UseMobileView ? "https://m.facebook.com/" : "https://www.facebook.com/";

    /// <summary>
    /// Starts the browser once, using a private profile folder. Signing in happens in the
    /// visible browser exactly as it would in Edge; the app never handles the password.
    /// </summary>
    private async Task EnsureBrowserAsync()
    {
        if (_browser.IsReady) return;

        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UFCFinder",
                "BrowserSession");

            await _browser.InitializeAsync(folder);

            WireBrowserEvents();

            _viewModel.SessionStatus = "Ready — sign in to Facebook in the browser panel.";
        }
        catch (Exception ex)
        {
            _viewModel.SessionStatus = "Browser unavailable: " + ex.Message;
            _viewModel.StatusText =
                "The embedded browser could not start. Public pages can still be scanned; "
                + "install the WebView2 runtime to scan signed-in pages.";
        }
    }

    private void ChooseLegalList_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select the legal venue list",
            Filter = "Spreadsheets (*.xlsx;*.xlsm;*.csv)|*.xlsx;*.xlsm;*.csv|Excel workbooks|*.xlsx;*.xlsm|CSV files|*.csv|All files|*.*",
            DefaultExt = ".xlsx",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.LegalListPath = dialog.FileName;
        }
    }

    private void ChooseWatchList_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select the watch list",
            Filter = "Spreadsheets (*.xlsx;*.xlsm;*.csv)|*.xlsx;*.xlsm;*.csv|Excel workbooks|*.xlsx;*.xlsm|CSV files|*.csv|All files|*.*",
            DefaultExt = ".xlsx",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.WatchListPath = dialog.FileName;
        }
    }

    private async void StartScan_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy) return;

        if (string.IsNullOrWhiteSpace(_viewModel.LegalListPath))
        {
            MessageBox.Show(this, "A legal list is required.", "UFC Event Host Finder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(_viewModel.WatchListPath))
        {
            MessageBox.Show(this, "A watch list is required.", "UFC Event Host Finder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var phrases = _viewModel.Phrases
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(phrase => phrase.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (phrases.Count == 0)
        {
            MessageBox.Show(this, "At least one search phrase is required.", "UFC Event Host Finder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        List<LegalVenue> legalVenues;
        List<WatchTarget> watchTargets;

        try
        {
            legalVenues = ListImporter.LoadLegalVenues(_viewModel.LegalListPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "There is a format problem with the legal list.\n\n" + ex.Message,
                "UFC Event Host Finder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            watchTargets = ListImporter.LoadWatchTargets(_viewModel.WatchListPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "There is a format problem with the watch list.\n\n" + ex.Message,
                "UFC Event Host Finder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _viewModel.IsBusy = true;
        _viewModel.ClearResults();

        _liveFindings.Clear();
        _liveLegal.Clear();
        _liveErrors.Clear();

        _scanStartedAt = DateTimeOffset.Now;
        _lastReport = null;
        _cancellation = new CancellationTokenSource();

        try
        {
            await EnsureBrowserAsync();

            var request = new ScanRequest
            {
                Phrases = phrases,
                LegalVenues = legalVenues,
                Targets = watchTargets,
                EnableAdLibrary = _viewModel.UseAdLibrary,
                EnableDiscovery = _viewModel.UseDiscovery,
                CollectScrollingPosts = _viewModel.CollectPosts,
                UseMobileView = _viewModel.UseMobileView,
                DelayBetweenPagesMs = (int)Math.Round(_viewModel.RequestDelaySeconds * 1000)
            };

            // The engine reports on the UI thread, so this handler can add rows straight to
            // the grid instead of queueing them behind the scan. That is what makes results
            // appear while the scan runs and survive a cancel.
            var progress = new SyncProgress<ScanProgress>(update =>
            {
                _viewModel.ProgressMaximum = update.Total;
                _viewModel.ProgressValue = Math.Min(update.Done, update.Total);
                _viewModel.StatusText = update.Message;

                foreach (var legal in update.NewLegalMatches)
                {
                    _liveLegal.Add(legal);
                    _viewModel.AddLegalMatch(legal);
                }

                foreach (var error in update.NewErrors)
                {
                    _liveErrors.Add(error);
                    _viewModel.AddError(error);
                }

                foreach (var finding in update.NewFindings)
                {
                    _liveFindings.Add(finding);
                    _viewModel.AddFinding(finding);
                }

                _viewModel.UpdateLiveSummary();
            });

            var engine = new ScanEngine(_browser, _http);
            var report = await engine.RunAsync(request, progress, _cancellation.Token);

            _lastReport = report;
            _viewModel.Apply(report);
            _viewModel.StatusText =
                $"Finished in {report.Duration.TotalMinutes:0.#} min — {report.Findings.Count} lead(s) "
                + $"from {report.PagesScanned} of {report.UrlsConsidered} URL(s).";
        }
        catch (OperationCanceledException)
        {
            // Keep what was already found so cancelling is not a total loss.
            _lastReport = _liveFindings.Count > 0 ? BuildPartialReport() : null;

            _viewModel.StatusText = _liveFindings.Count > 0
                ? $"Scan cancelled — kept {_liveFindings.Count} lead(s) found before cancelling."
                : "Scan cancelled.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "There was an issue processing your results.\n\n" + ex.Message,
                "UFC Event Host Finder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _viewModel.IsBusy = false;
            _viewModel.ProgressValue = 0;
            _viewModel.ProgressMaximum = 1;

            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    private void CancelScan_Click(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();
        _viewModel.StatusText = "Cancelling…";
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetReport(out var report)) return;

        var path = AskWhereToSave("CSV file (*.csv)|*.csv", ".csv", "ufc-findings");

        if (path is null) return;

        ReportExporter.SaveCsv(report, path);
        _viewModel.StatusText = "Saved CSV: " + path;
    }

    private void ExportXlsx_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetReport(out var report)) return;

        var path = AskWhereToSave("Excel workbook (*.xlsx)|*.xlsx", ".xlsx", "ufc-findings");

        if (path is null) return;

        try
        {
            ReportExporter.SaveXlsx(report, path);
            _viewModel.StatusText = "Saved workbook: " + path;
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, "The workbook could not be saved. Is it open in Excel?\n\n" + ex.Message,
                "UFC Event Host Finder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool TryGetReport(out ScanReport report)
    {
        if (_lastReport is null || _lastReport.Findings.Count == 0)
        {
            MessageBox.Show(this, "Run a scan first — there are no results to export.",
                "UFC Event Host Finder", MessageBoxButton.OK, MessageBoxImage.Information);

            report = new ScanReport();
            return false;
        }

        report = _lastReport;
        return true;
    }

    private string? AskWhereToSave(string filter, string extension, string fileStem)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save results",
            Filter = filter,
            DefaultExt = extension,
            FileName = $"{fileStem}-{DateTime.Now:yyyy-MM-dd-HHmm}{extension}"
        };

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private async void Go_Click(object sender, RoutedEventArgs e) => await NavigateToAddressAsync();

    private async void Address_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        await NavigateToAddressAsync();
    }

    private async Task NavigateToAddressAsync()
    {
        var target = _viewModel.AddressText.Trim();

        if (target.Length == 0) return;

        if (!target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            target = "https://" + target;
        }

        await EnsureBrowserAsync();
        _browser.Navigate(target);
    }

    private async void OpenFacebook_Click(object sender, RoutedEventArgs e)
    {
        await EnsureBrowserAsync();
        _browser.Navigate(FacebookHomeUrl);
    }

    private void ResetSession_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            this,
            "Sign out of the embedded browser? The next scan will need you to sign in again.",
            "UFC Event Host Finder",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        _browser.ClearSession();
        _viewModel.SessionStatus = "Signed out — sign in again in the browser panel.";
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2?.CanGoBack == true) Browser.CoreWebView2.GoBack();
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2?.CanGoForward == true) Browser.CoreWebView2.GoForward();
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.Reload();

    private void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Uri?.OriginalString)) return;

        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = "Could not open the link: " + ex.Message;
        }

        e.Handled = true;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _http.Dispose();
    }

    /// <summary>Keeps the address bar in step with whatever the scan is loading.</summary>
    private void WireBrowserEvents()
    {
        if (_browserEventsWired) return;

        var core = Browser.CoreWebView2;
        if (core is null) return;

        core.NavigationStarting += (_, args) => _viewModel.AddressText = args.Uri;

        core.SourceChanged += (_, _) =>
        {
            var source = Browser.CoreWebView2?.Source;

            if (!string.IsNullOrWhiteSpace(source))
            {
                _viewModel.AddressText = source;
            }
        };

        _browserEventsWired = true;
    }

    /// <summary>Assembles what has been found so far so a cancelled scan can still be exported.</summary>
    private ScanReport BuildPartialReport()
    {
        var report = new ScanReport
        {
            StartedAt = _scanStartedAt,
            CompletedAt = DateTimeOffset.Now
        };

        report.Findings.AddRange(_liveFindings);
        report.LegalMatches.AddRange(_liveLegal);
        report.Errors.AddRange(_liveErrors);

        return report;
    }

    /// <summary>Invokes progress callbacks immediately instead of posting them to the queue.</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SyncProgress(Action<T> handler) => _handler = handler;

        public void Report(T value) => _handler(value);
    }
}
