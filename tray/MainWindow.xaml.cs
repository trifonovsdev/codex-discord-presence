using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace CodexPresence;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer sessionTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly SemaphoreSlim dialogGate = new(1, 1);
    private readonly string version;

    private HealthSnapshot? snapshot;
    private string? connectionError;
    private DateTimeOffset? lastConfirmedAt;
    private PrivacyConfig privacy = new();
    private CancellationTokenSource? copyFeedbackCancellation;
    private bool closeForExit;
    private bool presenceActionPending;

    public event EventHandler? PauseRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? DiagnosticsRequested;
    public event EventHandler? PresentationVisibilityChanged;

    public bool IsVisible => AppWindow.IsVisible;

    public MainWindow(string version)
    {
        this.version = string.IsNullOrWhiteSpace(version) ? "unknown" : version;
        InitializeComponent();

        Title = "Codex Presence";
        AppTitleBar.Subtitle = "";
        FooterVersion.Text = $"v{this.version}";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };

        WindowSizing.ResizeInDips(this, 680, 560);
        WindowSizing.SetMinimumInDips(this, 640, 480);
        AppWindow.Closing += AppWindow_Closing;
        Closed += MainWindow_Closed;
        RootLayout.ActualThemeChanged += (_, _) => Render();

        sessionTimer.Tick += (_, _) => RenderTime();
        Render();
    }

    public void SetUpdateProgress(UpdateProgress? progress)
    {
        UpdateBar.IsOpen = progress is not null;
        if (progress is null) return;
        UpdateBar.Message = progress.Percent is { } percent ? $"{progress.Stage} · {percent:0}%" : progress.Stage;
        UpdateDownloadProgress.IsIndeterminate = progress.Percent is null;
        UpdateDownloadProgress.Value = progress.Percent ?? 0;
    }

    public void SetPresenceActionPending(bool pending)
    {
        presenceActionPending = pending;
        Render();
    }

    public void UpdateSnapshot(HealthSnapshot? snapshot, string? connectionError = null)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => UpdateSnapshot(snapshot, connectionError));
            return;
        }

        this.snapshot = snapshot;
        this.connectionError = connectionError;
        if (snapshot is not null && connectionError is null) lastConfirmedAt = DateTimeOffset.Now;
        Render();
    }

    public void UpdatePrivacy(PrivacyConfig privacy)
    {
        ArgumentNullException.ThrowIfNull(privacy);
        if (!DispatcherQueue.HasThreadAccess)
        {
            var copy = CopyPrivacy(privacy);
            DispatcherQueue.TryEnqueue(() => UpdatePrivacy(copy));
            return;
        }

        this.privacy = CopyPrivacy(privacy);
        Render();
    }

    public void ShowWindow()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(ShowWindow);
            return;
        }

        var wasVisible = IsVisible;
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        AppWindow.Show();
        sessionTimer.Start();
        Render();
        if (!wasVisible) PresentationVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public void HideWindow()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(HideWindow);
            return;
        }

        if (!IsVisible) return;
        AppWindow.Hide();
        sessionTimer.Stop();
        PresentationVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public void CloseForExit()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(CloseForExit);
            return;
        }

        closeForExit = true;
        AppWindow.Closing -= AppWindow_Closing;
        Close();
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        await dialogGate.WaitAsync();
        try
        {
            if (!IsVisible) ShowWindow();
            var xamlRoot = await EnsureXamlRootAsync();
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = title,
                Content = message,
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
            };
            await dialog.ShowAsync();
        }
        finally
        {
            dialogGate.Release();
        }
    }

    public async Task<bool> ConfirmAsync(string title, string message, string primaryText = "Continue")
    {
        await dialogGate.WaitAsync();
        try
        {
            if (!IsVisible) ShowWindow();
            var xamlRoot = await EnsureXamlRootAsync();
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = title,
                Content = message,
                PrimaryButtonText = primaryText,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            dialogGate.Release();
        }
    }

    private void Render()
    {
        var presentation = PresencePresentation.Create(snapshot, privacy, DateTimeOffset.Now, connectionError, lastConfirmedAt);

        var contextChanged = ProjectName.Text != presentation.Project || CurrentFile.Text != presentation.CurrentFile;
        ConnectionStatus.Text = presentation.Connection;
        ConnectionDot.Fill = ToneBrush(presentation.ConnectionTone);
        ActivityContext.Text = presentation.ActivityContext;
        ProjectName.Text = presentation.Project;
        CurrentFile.Text = presentation.CurrentFile;
        ToolTipService.SetToolTip(CurrentFile, presentation.CurrentFile);
        ToolTipService.SetToolTip(ProjectName, presentation.Project);
        ToolTipService.SetToolTip(WorkspaceValue, presentation.Workspace);
        CopyPathButton.IsEnabled = presentation.CopyPath is not null;

        SourceValue.Text = presentation.Source;
        WorkspaceValue.Text = presentation.Workspace;
        SessionValue.Text = presentation.Session;
        SharingSummary.Text = presentation.SharingSummary;

        WarningBar.IsOpen = presentation.WarningMessage is not null;
        WarningBar.Title = presentation.WarningTitle ?? string.Empty;
        WarningBar.Message = presentation.WarningMessage ?? string.Empty;
        WarningBar.Severity = presentation.WarningTone == PresenceTone.Danger
            ? InfoBarSeverity.Error
            : InfoBarSeverity.Warning;

        PreviewLabel.Text = presentation.PreviewLabel;
        PreviewTitle.Text = presentation.PreviewTitle;
        PreviewPrimaryLine.Text = presentation.PreviewPrimary;
        PreviewSecondaryLine.Text = presentation.PreviewSecondary;
        PreviewElapsed.Text = presentation.PreviewElapsed;
        PreviewStatusDot.Fill = ToneBrush(presentation.PreviewTone);
        PreviewTimerRow.Visibility = presentation.ShowPreviewElapsed ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(
            DiscordPreview,
            $"Discord activity preview. {presentation.PreviewTitle}. {presentation.PreviewPrimary}. {presentation.PreviewSecondary}");

        if (contextChanged && RootLayout.IsLoaded && IsVisible) Motion.Reveal(DiscordPreview);
        PauseButton.IsEnabled = presentation.PauseEnabled && !presenceActionPending;
        PauseButtonText.Text = presenceActionPending ? "Updating…" : presentation.PauseText;
        PauseIcon.Glyph = snapshot?.PresenceEnabled == false ? "\uE768" : "\uE769";
        AutomationProperties.SetName(PauseButton, presentation.PauseText);
    }

    private void RenderTime()
    {
        if (connectionError is not null) return;
        var (session, elapsed) = PresencePresentation.SessionTiming(snapshot, DateTimeOffset.Now);
        SessionValue.Text = session;
        PreviewElapsed.Text = elapsed;
    }

    private Brush ToneBrush(PresenceTone tone)
    {
        var key = tone switch
        {
            PresenceTone.Success => "SuccessBrush",
            PresenceTone.Warning => "WarningBrush",
            PresenceTone.Danger => "DangerBrush",
            _ => "TextMutedBrush",
        };
        return (Brush)Application.Current.Resources[key];
    }

    private Task<XamlRoot> EnsureXamlRootAsync()
    {
        if (RootLayout.XamlRoot is { } availableRoot)
            return Task.FromResult(availableRoot);

        var completion = new TaskCompletionSource<XamlRoot>(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler? loaded = null;
        loaded = (_, _) =>
        {
            if (RootLayout.XamlRoot is not { } loadedRoot) return;
            RootLayout.Loaded -= loaded;
            completion.TrySetResult(loadedRoot);
        };

        RootLayout.Loaded += loaded;
        if (RootLayout.XamlRoot is { } rootAfterSubscription)
        {
            RootLayout.Loaded -= loaded;
            completion.TrySetResult(rootAfterSubscription);
        }

        return completion.Task;
    }

    private async void CopyPathButton_Click(object sender, RoutedEventArgs e)
    {
        var path = PresencePresentation.Create(snapshot, privacy, DateTimeOffset.Now).CopyPath;
        if (path is null) return;

        try
        {
            var package = new DataPackage();
            package.SetText(path);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            await ShowCopyFeedbackAsync();
        }
        catch (Exception error)
        {
            await ShowMessageAsync("Could not copy the file path", error.Message);
        }
    }

    private async Task ShowCopyFeedbackAsync()
    {
        copyFeedbackCancellation?.Cancel();
        copyFeedbackCancellation?.Dispose();
        copyFeedbackCancellation = new CancellationTokenSource();
        var token = copyFeedbackCancellation.Token;
        Motion.Fade(CopyStatus, 1f);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1400), token);
            Motion.Fade(CopyStatus, 0f);
        }
        catch (OperationCanceledException)
        {
            // A newer copy action owns the visible feedback.
        }
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e) =>
        PauseRequested?.Invoke(this, EventArgs.Empty);

    private void SettingsButton_Click(object sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void DiagnosticsButton_Click(object sender, RoutedEventArgs e) =>
        DiagnosticsRequested?.Invoke(this, EventArgs.Empty);

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (closeForExit) return;
        args.Cancel = true;
        HideWindow();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        sessionTimer.Stop();
        copyFeedbackCancellation?.Cancel();
        copyFeedbackCancellation?.Dispose();
        dialogGate.Dispose();
    }

    private static PrivacyConfig CopyPrivacy(PrivacyConfig source) => new()
    {
        Preset = source.Preset,
        ShowProject = source.ShowProject,
        ShowTaskTitle = source.ShowTaskTitle,
        ShowFile = source.ShowFile,
        ShowTimer = source.ShowTimer,
        FileMode = source.FileMode,
    };
}
