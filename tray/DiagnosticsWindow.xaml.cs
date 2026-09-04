using System.Collections.ObjectModel;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace CodexPresence;

public sealed partial class DiagnosticsWindow : Window
{
    private readonly DiagnosticsService diagnostics;
    private readonly ObservableCollection<DiagnosticResultViewModel> displayedResults = [];
    private IReadOnlyList<DiagnosticItem> latestResults = [];
    private CancellationTokenSource? runCancellation;
    private bool firstRunStarted;
    private bool isRunning;
    private bool isClosed;

    public DiagnosticsWindow(DiagnosticsService diagnostics)
    {
        this.diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));

        InitializeComponent();
        ResultsList.ItemsSource = displayedResults;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };

        WindowSizing.ResizeInDips(this, 760, 620);
        AppWindow.Closing += OnWindowClosing;
        RootLayout.ActualThemeChanged += (_, _) => RefreshStatusBrushes();
    }

    /// <summary>Shows Doctor and starts its checks on the first presentation.</summary>
    public void ShowWindow()
    {
        if (isClosed)
        {
            throw new InvalidOperationException("A closed Doctor window cannot be shown again.");
        }

        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();

        if (firstRunStarted)
        {
            return;
        }

        firstRunStarted = true;
        _ = RunDiagnosticsAsync();
    }

    private async void RunAgainButton_Click(object sender, RoutedEventArgs e)
    {
        await RunDiagnosticsAsync();
    }

    private async Task RunDiagnosticsAsync()
    {
        if (isRunning || isClosed)
        {
            return;
        }

        isRunning = true;
        var cancellation = new CancellationTokenSource();
        runCancellation = cancellation;
        ShowRunningState();

        try
        {
            var runResults = await diagnostics.RunAsync(cancellation.Token);
            if (cancellation.IsCancellationRequested || isClosed)
            {
                return;
            }

            ShowResults(runResults);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Closing the window or starting teardown cancels in-flight network checks.
        }
        catch (Exception error)
        {
            if (!isClosed)
            {
                ShowResults([new DiagnosticItem("Doctor", false, error.Message)]);
            }
        }
        finally
        {
            if (ReferenceEquals(runCancellation, cancellation))
            {
                runCancellation = null;
            }

            cancellation.Dispose();
            isRunning = false;

            if (!isClosed)
            {
                RunningProgress.IsActive = false;
                RunningProgress.Visibility = Visibility.Collapsed;
                SummaryDot.Visibility = Visibility.Visible;
                RunAgainButton.IsEnabled = true;
                CopyReportButton.IsEnabled = latestResults.Count > 0;
            }
        }
    }

    private void ShowRunningState()
    {
        RunAgainButton.IsEnabled = false;
        CopyReportButton.IsEnabled = false;
        SummaryText.Text = "Running checks";
        SummaryDot.Visibility = Visibility.Collapsed;
        RunningProgress.Visibility = Visibility.Visible;
        RunningProgress.IsActive = true;

        if (latestResults.Count == 0)
        {
            ResultsList.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            EmptyStateTitle.Text = "Running system checks";
            EmptyStateDescription.Text = "Inspecting local services, Discord, hooks, and remote hosts…";
            AutomationProperties.SetName(EmptyState, "Diagnostics are running");
        }
    }

    private void ShowResults(IReadOnlyList<DiagnosticItem> results)
    {
        latestResults = results.ToArray();
        RefreshStatusBrushes();

        var passed = latestResults.Count(item => item.Passed == true);
        var failed = latestResults.Count(item => item.Passed == false);
        var unknown = latestResults.Count(item => item.Passed is null);

        if (latestResults.Count == 0)
        {
            ResultsList.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            EmptyStateTitle.Text = "No checks were returned";
            EmptyStateDescription.Text = "Run Doctor again. If this continues, restart Codex Presence.";
            AutomationProperties.SetName(EmptyState, "No diagnostic results were returned");
            SummaryText.Text = "No results";
            SummaryDot.Fill = ThemeBrush("TextSecondaryBrush");
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        ResultsList.Visibility = Visibility.Visible;

        if (failed == 0 && unknown == 0)
        {
            SummaryText.Text = $"All {passed} checks passed";
            SummaryDot.Fill = ThemeBrush("SuccessBrush");
        }
        else
        {
            SummaryText.Text = $"{failed} need attention · {unknown} not checked";
            SummaryDot.Fill = ThemeBrush("DangerBrush");
        }

        AutomationProperties.SetName(ResultsList, $"Diagnostic results. {passed} passed, {failed} failed, {unknown} not checked.");
    }

    private void RefreshStatusBrushes()
    {
        displayedResults.Clear();
        foreach (var item in latestResults)
        {
            displayedResults.Add(new DiagnosticResultViewModel(item));
        }

        SummaryDot.Fill = latestResults.Count == 0
            ? ThemeBrush("TextSecondaryBrush")
            : ThemeBrush(latestResults.Any(item => item.Passed == false) ? "DangerBrush" : "SuccessBrush");
    }

    private async void CopyReportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = new DataPackage
            {
                RequestedOperation = DataPackageOperation.Copy,
            };
            package.SetText(BuildReport());
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (Exception error)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootLayout.XamlRoot,
                Title = "Couldn’t copy the report",
                Content = $"Windows Clipboard rejected the report. Try again.\n\n{error.Message}",
                CloseButtonText = "OK",
                DefaultButton = ContentDialogButton.Close,
            };
            await dialog.ShowAsync();
        }
    }

    private string BuildReport()
    {
        var lines = new List<string>
        {
            "Codex Presence Doctor",
            $"Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}",
            "Format: [PASS|FAIL|UNKNOWN] Check name: details",
            string.Empty,
        };

        lines.AddRange(latestResults.Select(item =>
            $"[{(item.Passed is null ? "UNKNOWN" : item.Passed == true ? "PASS" : "FAIL")}] {item.Name}: {item.Detail}"));

        return string.Join(Environment.NewLine, lines);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        isClosed = true;
        runCancellation?.Cancel();
    }

    private static Brush ThemeBrush(string resourceKey)
    {
        return Application.Current.Resources[resourceKey] as Brush
            ?? new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }
}

public sealed class DiagnosticResultViewModel
{
    public DiagnosticResultViewModel(DiagnosticItem item)
    {
        Name = item.Name;
        Detail = item.Detail;
        Passed = item.Passed;
        StatusLabel = Passed is null ? "Not checked" : Passed == true ? "Passed" : "Needs attention";
        StatusGlyph = Passed is null ? "\uE946" : Passed == true ? "\uE73E" : "\uE7BA";
        StatusBrush = Application.Current.Resources[Passed is null ? "TextMutedBrush" : Passed == true ? "SuccessBrush" : "DangerBrush"] as Brush
            ?? new SolidColorBrush(Passed == true ? Microsoft.UI.Colors.SeaGreen : Microsoft.UI.Colors.IndianRed);
        AccessibilityLabel = $"{Name}: {StatusLabel}. {Detail}";
    }

    public string Name { get; }

    public string Detail { get; }

    public bool? Passed { get; }

    public string StatusLabel { get; }

    public string StatusGlyph { get; }

    public Brush StatusBrush { get; }

    public string AccessibilityLabel { get; }
}
