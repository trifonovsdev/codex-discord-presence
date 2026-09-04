using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CodexPresence;

public sealed partial class SettingsWindow : Window
{
    [GeneratedRegex("^[A-Za-z0-9._@:-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_./~-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RootPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();

    private static readonly (string Label, string Value)[] Languages =
    [
        ("English", "en"),
        ("Русский", "ru"),
    ];

    private static readonly (string Label, int Seconds)[] PollIntervals =
    [
        ("3 seconds", 3),
        ("5 seconds", 5),
        ("7 seconds", 7),
        ("10 seconds", 10),
        ("15 seconds", 15),
        ("30 seconds", 30),
    ];

    private readonly ConfigStore store;
    private readonly RemoteService remoteService;
    private readonly PresenceConfig config;
    private readonly ObservableCollection<RemoteRow> remoteRows = [];
    private CancellationTokenSource? remoteActionCancellation;
    private string remoteActionBrushKey = "TextSecondaryBrush";
    private bool suppressPresetChange;
    private bool isClosing;

    public event EventHandler? Saved;

    public bool HasSaved { get; private set; }

    public SettingsWindow(ConfigStore store, RemoteService remoteService, PresenceConfig? previewConfig = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.remoteService = remoteService ?? throw new ArgumentNullException(nameof(remoteService));
        config = previewConfig ?? this.store.Load();
        config.Privacy ??= new PrivacyConfig();
        config.Remote ??= new RemoteConfig();
        config.Remote.Hosts ??= [];
        config.Updates ??= new UpdateConfig();

        InitializeComponent();

        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        WindowSizing.ResizeInDips(this, 740, 620);
        WindowSizing.SetMinimumInDips(this, 700, 480);
        AppWindow.Closing += (_, _) => CancelRemoteAction();
        Closed += (_, _) => CancelRemoteAction();
        RootGrid.ActualThemeChanged += (_, _) => RefreshStatusBrush();

        LanguageSelect.ItemsSource = Languages.Select(item => item.Label).ToArray();
        PresetSelect.ItemsSource = new[] { "minimal", "standard", "detailed" };
        FileModeSelect.ItemsSource = new[] { "name", "relative" };
        PollIntervalSelect.ItemsSource = PollIntervals.Select(item => item.Label).ToArray();
        RemoteList.ItemsSource = remoteRows;
        remoteRows.CollectionChanged += (_, _) => UpdateRemoteEmptyState();

        LoadValues();
        ShowPage("general");
    }

    public void ShowWindow()
    {
        if (isClosing) return;
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        AppWindow.Show();
        Activate();
    }

    private void SectionButtonChecked(object sender, RoutedEventArgs args)
    {
        if (sender is RadioButton { Tag: string tag }) ShowPage(tag);
    }

    internal void ShowPage(string tag)
    {
        if (GeneralPage is null || PrivacyPage is null || RemotePage is null) return;
        GeneralPage.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        PrivacyPage.Visibility = tag == "privacy" ? Visibility.Visible : Visibility.Collapsed;
        RemotePage.Visibility = tag == "remote" ? Visibility.Visible : Visibility.Collapsed;
        SetActiveSection(tag);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            SettingsSidebar, $"Settings sections. {tag switch { "privacy" => "Privacy", "remote" => "SSH workspaces", _ => "General" }} selected");
    }

    private void SetActiveSection(string tag)
    {
        GeneralNavButton.IsChecked = tag == "general";
        PrivacyNavButton.IsChecked = tag == "privacy";
        RemoteNavButton.IsChecked = tag == "remote";
    }

    private void LoadValues()
    {
        suppressPresetChange = true;
        try
        {
            PresenceToggle.IsOn = config.PresenceEnabled;
            ActivityNameInput.Text = config.ActivityName;
            try
            {
                StartupToggle.IsOn = store.StartsWithWindows;
            }
            catch
            {
                StartupToggle.IsOn = false;
            }

            UpdatesToggle.IsOn = config.Updates.Enabled;
            LanguageSelect.SelectedItem = Languages.FirstOrDefault(item => item.Value == config.Language).Label
                ?? Languages[0].Label;
            PresetSelect.SelectedItem = NormalizeOption(config.Privacy.Preset, "standard", "minimal", "standard", "detailed");
            TaskTitleToggle.IsOn = config.Privacy.ShowTaskTitle;
            ProjectToggle.IsOn = config.Privacy.ShowProject;
            FileToggle.IsOn = config.Privacy.ShowFile;
            TimerToggle.IsOn = config.Privacy.ShowTimer;
            FileModeSelect.SelectedItem = NormalizeOption(config.Privacy.FileMode, "relative", "name", "relative");

            var configuredSeconds = Math.Clamp(config.Remote.PollIntervalMs / 1000, 3, 60);
            PollIntervalSelect.SelectedItem = PollIntervals
                .OrderBy(item => Math.Abs(item.Seconds - configuredSeconds))
                .First()
                .Label;

            foreach (var remote in config.Remote.Hosts)
                remoteRows.Add(RemoteRow.FromConfig(remote));

            if (remoteRows.Count == 0 && !string.IsNullOrWhiteSpace(config.Remote.Host))
            {
                remoteRows.Add(new RemoteRow
                {
                    Name = config.Remote.Host,
                    Host = config.Remote.Host,
                });
            }
        }
        finally
        {
            suppressPresetChange = false;
        }

        UpdateRemoteEmptyState();
    }

    private void PresetSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (suppressPresetChange) return;
        ApplyPreset(SelectedText(PresetSelect, "standard"));
    }

    private void ApplyPreset(string value)
    {
        TaskTitleToggle.IsOn = false;
        ProjectToggle.IsOn = true;
        FileToggle.IsOn = value != "minimal";
        TimerToggle.IsOn = true;
        FileModeSelect.SelectedItem = value == "minimal" ? "name" : "relative";
    }

    private void AddRemoteClicked(object sender, RoutedEventArgs args)
    {
        var row = new RemoteRow();
        remoteRows.Add(row);
        RemoteList.SelectedItem = row;
        RemoteList.ScrollIntoView(row);
    }

    private void RemoveRemoteClicked(object sender, RoutedEventArgs args)
    {
        if (RemoteList.SelectedItem is not RemoteRow row) return;
        remoteRows.Remove(row);
    }

    private async void TestRemoteClicked(object sender, RoutedEventArgs args) =>
        await RunRemoteActionAsync(install: false);

    private async void InstallRemoteClicked(object sender, RoutedEventArgs args) =>
        await RunRemoteActionAsync(install: true);

    private async Task RunRemoteActionAsync(bool install)
    {
        if (RemoteList.SelectedItem is not RemoteRow row || string.IsNullOrWhiteSpace(row.Host))
        {
            await ShowDialogAsync(
                "Select a workspace",
                "Add a row, select it, and fill in user@host before testing the connection.");
            return;
        }

        if (Validate(row) is { } problem)
        {
            await ShowDialogAsync("Check the workspace", problem);
            return;
        }

        remoteActionCancellation?.Cancel();
        remoteActionCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        remoteActionCancellation = cancellation;
        SetRemoteActionsEnabled(false);
        SetRemoteActionStatus(
            install ? $"Installing helper on {row.Host}…" : $"Testing SSH connection to {row.Host}…",
            "TextSecondaryBrush",
            isRunning: true);

        try
        {
            var result = install
                ? await remoteService.InstallHelperAsync(row.ToConfig(), cancellation.Token)
                : await remoteService.TestAsync(row.ToConfig(), cancellation.Token);

            if (cancellation.IsCancellationRequested || isClosing) return;

            var summary = result.Ok
                ? install ? "Helper installed" : "SSH connection ready"
                : install ? "Helper installation failed" : "SSH connection failed";
            SetRemoteActionStatus(summary, result.Ok ? "SuccessBrush" : "DangerBrush", isRunning: false);
            await ShowDialogAsync(result.Ok ? "Connection ready" : "SSH failed", result.Output);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!isClosing) SetRemoteActionStatus("SSH action cancelled", "TextSecondaryBrush", isRunning: false);
        }
        catch (Exception error)
        {
            if (!isClosing)
            {
                SetRemoteActionStatus("SSH action failed", "DangerBrush", isRunning: false);
                await ShowDialogAsync("SSH action failed", error.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(remoteActionCancellation, cancellation)) remoteActionCancellation = null;
            cancellation.Dispose();
            if (!isClosing) SetRemoteActionsEnabled(true);
        }
    }

    private void SetRemoteActionsEnabled(bool value)
    {
        AddRemoteButton.IsEnabled = value;
        RemoveRemoteButton.IsEnabled = value;
        TestRemoteButton.IsEnabled = value;
        InstallRemoteButton.IsEnabled = value;
        RemoteList.IsEnabled = value;
    }

    private void SetRemoteActionStatus(string message, string brushKey, bool isRunning)
    {
        remoteActionBrushKey = brushKey;
        RemoteActionStatus.Text = message;
        RefreshStatusBrush();
        RemoteActionStatus.Visibility = Visibility.Visible;
        RemoteProgress.IsActive = isRunning;
        RemoteProgress.Visibility = isRunning ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshStatusBrush() =>
        RemoteActionStatus.Foreground = (Brush)Application.Current.Resources[remoteActionBrushKey];

    private async void SaveClicked(object sender, RoutedEventArgs args)
    {
        var activityName = WhitespacePattern().Replace(ActivityNameInput.Text ?? string.Empty, " ").Trim();
        if (activityName.Length < 2)
        {
            await ShowDialogAsync("Check the activity name", "Enter between 2 and 128 characters.");
            ActivityNameInput.Focus(FocusState.Programmatic);
            return;
        }

        var filled = remoteRows.Where(row => !string.IsNullOrWhiteSpace(row.Host)).ToList();
        if (filled.Select(Validate).FirstOrDefault(problem => problem is not null) is { } invalid)
        {
            await ShowDialogAsync("Check the SSH workspaces", invalid);
            return;
        }

        config.PresenceEnabled = PresenceToggle.IsOn == true;
        config.ActivityName = activityName;
        config.Updates.Enabled = UpdatesToggle.IsOn == true;
        config.Language = Languages.FirstOrDefault(item => item.Label == SelectedText(LanguageSelect, "English")).Value
            ?? "en";
        config.Privacy = new PrivacyConfig
        {
            Preset = SelectedText(PresetSelect, "standard"),
            ShowTaskTitle = TaskTitleToggle.IsOn == true,
            ShowProject = ProjectToggle.IsOn == true,
            ShowFile = FileToggle.IsOn == true,
            ShowTimer = TimerToggle.IsOn == true,
            FileMode = SelectedText(FileModeSelect, "relative"),
        };
        config.Remote.Host = string.Empty;
        config.Remote.Hosts = filled.Select(row => row.ToConfig()).ToList();
        config.Remote.PollIntervalMs = ParseSeconds(SelectedText(PollIntervalSelect, "7 seconds")) * 1000;

        try
        {
            store.Save(config);
            store.StartsWithWindows = StartupToggle.IsOn == true;
        }
        catch (Exception error)
        {
            await ShowDialogAsync("Settings were not saved", error.Message);
            return;
        }

        HasSaved = true;
        Saved?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void CancelClicked(object sender, RoutedEventArgs args) => Close();

    private async Task ShowDialogAsync(string title, string message)
    {
        if (isClosing || RootGrid.XamlRoot is null) return;

        var body = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(message) ? "No additional details were returned." : message,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            MaxWidth = 520,
        };
        var scroller = new ScrollViewer
        {
            MaxHeight = 340,
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = scroller,
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowAsync();
    }

    private void UpdateRemoteEmptyState()
    {
        if (RemoteEmptyState is null) return;
        RemoteEmptyState.Visibility = remoteRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CancelRemoteAction()
    {
        isClosing = true;
        remoteActionCancellation?.Cancel();
    }

    private static string NormalizeOption(string? value, string fallback, params string[] options) =>
        value is not null && options.Contains(value, StringComparer.OrdinalIgnoreCase)
            ? options.First(option => string.Equals(option, value, StringComparison.OrdinalIgnoreCase))
            : fallback;

    private static string SelectedText(ComboBox comboBox, string fallback) =>
        comboBox.SelectedItem?.ToString() ?? fallback;

    private static int ParseSeconds(string value) =>
        int.TryParse(value.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], out var seconds) ? seconds : 7;

    private static string? Validate(RemoteRow row)
    {
        var host = row.Host.Trim();
        if (!HostPattern().IsMatch(host))
        {
            return $"“{row.Host}” is not a valid SSH host.\n\n" +
                   "Use only letters, numbers, dots, colons, dashes, underscores and @, for example dev@example.com.";
        }

        var roots = row.Roots.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var badRoot = roots.FirstOrDefault(root => !RootPattern().IsMatch(root));
        if (badRoot is not null)
        {
            return $"“{badRoot}” is not a valid workspace root.\n\n" +
                   "Use absolute POSIX paths such as /srv/store, separated by semicolons.";
        }

        return null;
    }

    public sealed class RemoteRow
    {
        private const string DefaultMonitorPath = "~/.local/share/CodexDiscordPresence/remote-monitor.py";

        public string Name { get; set; } = "Remote";
        public string Host { get; set; } = string.Empty;
        public string Roots { get; set; } = string.Empty;
        public string MonitorPath { get; set; } = DefaultMonitorPath;

        public RemoteHostConfig ToConfig() => new()
        {
            Name = Name.Trim(),
            Host = Host.Trim(),
            Roots = Roots.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            MonitorPath = MonitorPath,
        };

        public static RemoteRow FromConfig(RemoteHostConfig value) => new()
        {
            Name = value.Name,
            Host = value.Host,
            Roots = string.Join("; ", value.Roots ?? []),
            MonitorPath = string.IsNullOrWhiteSpace(value.MonitorPath) ? DefaultMonitorPath : value.MonitorPath,
        };
    }
}
