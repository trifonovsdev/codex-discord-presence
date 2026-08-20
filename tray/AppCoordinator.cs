using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace CodexPresence;

/// <summary>Coordinates the WinUI windows, native tray icon, and daemon lifetime.</summary>
public sealed class AppCoordinator : IDisposable
{
    private const int ForegroundPollMs = 2000;
    private const int BackgroundPollMs = 8000;

    private readonly ConfigStore configStore = new();
    private readonly RemoteService remote = new();
    private readonly UpdateService updates = new();
    private readonly DaemonService daemon;
    private readonly DiagnosticsService diagnostics;
    private readonly MainWindow dashboard;
    private readonly TrayIcon trayIcon;
    private readonly DispatcherQueue dispatcher;
    private readonly DispatcherTimer timer = new();
    private readonly RegisteredWaitHandle? activationWait;

    private SettingsWindow? settingsWindow;
    private DiagnosticsWindow? diagnosticsWindow;
    private HealthSnapshot? latest;
    private int refreshInProgress;
    private int updateInProgress;
    private int exitStarted;
    private int disposed;
    private bool exiting;

    public static string Version => Assembly.GetExecutingAssembly().GetName().Version is { } version
        ? $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}"
        : "2.3.4";

    /// <summary>Raised after all app-owned resources and the daemon are stopped.</summary>
    public event EventHandler? ExitCompleted;

    public AppCoordinator(bool showOnStart, WaitHandle? activationSignal = null)
    {
        dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("AppCoordinator must be created on the WinUI thread.");

        if (!File.Exists(AppPaths.ConfigPath)) configStore.Save(new PresenceConfig());

        daemon = new DaemonService(configStore);
        diagnostics = new DiagnosticsService(daemon, configStore, remote);
        dashboard = new MainWindow(Version);
        var initialConfig = configStore.Load();
        initialConfig.Privacy ??= new PrivacyConfig();
        dashboard.UpdatePrivacy(initialConfig.Privacy);
        trayIcon = new TrayIcon();

        WireEvents();

        if (activationSignal is not null)
        {
            activationWait = ThreadPool.RegisterWaitForSingleObject(
                activationSignal,
                static (state, _) => ((AppCoordinator)state!).QueueActivation(),
                this,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }

        timer.Interval = TimeSpan.FromMilliseconds(showOnStart ? ForegroundPollMs : BackgroundPollMs);
        timer.Tick += OnTimerTick;
        timer.Start();
        _ = InitializeAsync(showOnStart);
    }

    private void WireEvents()
    {
        trayIcon.OpenRequested += (_, _) => ShowDashboard();
        trayIcon.ToggleRequested += async (_, _) => await TogglePresenceAsync();
        trayIcon.SettingsRequested += (_, _) => ShowSettings();
        trayIcon.DiagnosticsRequested += (_, _) => ShowDiagnostics();
        trayIcon.UpdateRequested += async (_, _) => await CheckUpdatesAsync(interactive: true);
        trayIcon.RestartRequested += async (_, _) => await RestartAsync();
        trayIcon.ExitRequested += async (_, _) => await ExitAsync();

        dashboard.PauseRequested += async (_, _) => await TogglePresenceAsync();
        dashboard.SettingsRequested += (_, _) => ShowSettings();
        dashboard.DiagnosticsRequested += (_, _) => ShowDiagnostics();
        dashboard.PresentationVisibilityChanged += (_, _) => UpdatePollingInterval();
    }

    private async Task InitializeAsync(bool showOnStart)
    {
        try
        {
            try
            {
                await daemon.EnsureRunningAsync();
            }
            catch (Exception error)
            {
                trayIcon.ShowBalloon("Codex Presence could not start", error.Message, isError: true);
            }

            await RefreshAsync();
            if (exiting) return;

            if (showOnStart) ShowDashboard();

            var config = configStore.Load();
            if (config.Updates.Enabled && ShouldCheckForUpdates(config.Updates.CheckIntervalHours))
                await CheckUpdatesAsync(interactive: false);
        }
        catch (Exception error)
        {
            if (!exiting)
                trayIcon.ShowBalloon("Codex Presence", error.Message, isError: true);
        }
    }

    private async void OnTimerTick(object? sender, object args)
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception error)
        {
            if (!exiting)
                trayIcon.ShowBalloon("Could not refresh Codex Presence", error.Message, isError: true);
        }
    }

    private void UpdatePollingInterval()
    {
        if (exiting) return;
        timer.Interval = TimeSpan.FromMilliseconds(dashboard.IsVisible ? ForegroundPollMs : BackgroundPollMs);
    }

    private void QueueActivation()
    {
        if (exiting || Volatile.Read(ref disposed) != 0) return;
        _ = dispatcher.TryEnqueue(() =>
        {
            if (!exiting) ShowDashboard();
        });
    }

    public void ShowDashboard()
    {
        if (exiting) return;
        dashboard.UpdateSnapshot(latest);
        dashboard.ShowWindow();
        UpdatePollingInterval();
    }

    private async Task RefreshAsync()
    {
        if (exiting || Interlocked.CompareExchange(ref refreshInProgress, 1, 0) != 0) return;

        HealthSnapshot? snapshot;
        try
        {
            snapshot = await daemon.HealthAsync();
        }
        finally
        {
            Interlocked.Exchange(ref refreshInProgress, 0);
        }

        if (exiting) return;
        latest = snapshot;
        dashboard.UpdateSnapshot(snapshot);

        if (snapshot is null)
        {
            var presenceEnabled = configStore.Load().PresenceEnabled;
            trayIcon.UpdateStatus(
                "Codex Presence — offline",
                "Service unavailable",
                "Run diagnostics for details",
                presenceEnabled);
            return;
        }

        var status = snapshot.PresenceEnabled
            ? snapshot.RpcReady ? "Discord connected" : "Waiting for Discord"
            : "Presence paused";
        var activity = $"{snapshot.Project ?? "Project not detected"}  ·  {Shorten(snapshot.File, 48)}";
        trayIcon.UpdateStatus(
            $"Codex Presence — {snapshot.Project ?? "waiting"}",
            status,
            activity,
            snapshot.PresenceEnabled);
    }

    private async Task TogglePresenceAsync()
    {
        if (exiting) return;
        try
        {
            await daemon.ControlAsync(latest?.PresenceEnabled == false ? "resume" : "pause");
            await RefreshAsync();
        }
        catch (Exception error)
        {
            await ShowErrorAsync("Could not change presence state", error);
        }
    }

    private void ShowSettings()
    {
        if (exiting) return;
        if (settingsWindow is not null)
        {
            settingsWindow.ShowWindow();
            return;
        }

        var window = new SettingsWindow(configStore, remote);
        settingsWindow = window;
        window.Saved += OnSettingsSaved;
        window.Closed += (_, _) =>
        {
            window.Saved -= OnSettingsSaved;
            if (ReferenceEquals(settingsWindow, window)) settingsWindow = null;
        };
        window.ShowWindow();
    }

    private async void OnSettingsSaved(object? sender, EventArgs args)
    {
        if (exiting) return;
        dashboard.UpdatePrivacy(configStore.Load().Privacy);
        try
        {
            daemon.InvalidateEndpoint();
            await daemon.RestartAsync();
            await RefreshAsync();
        }
        catch (Exception error)
        {
            await ShowErrorAsync("Settings were saved, but restart failed", error);
        }
    }

    private void ShowDiagnostics()
    {
        if (exiting) return;
        if (diagnosticsWindow is not null)
        {
            diagnosticsWindow.ShowWindow();
            return;
        }

        var window = new DiagnosticsWindow(diagnostics);
        diagnosticsWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(diagnosticsWindow, window)) diagnosticsWindow = null;
        };
        window.ShowWindow();
    }

    private async Task CheckUpdatesAsync(bool interactive)
    {
        if (exiting || Interlocked.CompareExchange(ref updateInProgress, 1, 0) != 0) return;
        try
        {
            var config = configStore.Load();
            var release = await updates.CheckAsync(config.Updates.Repository);
            MarkUpdateCheck();
            if (exiting) return;

            if (release is not null && release.Version > updates.CurrentVersion)
            {
                ShowDashboard();
                var body = $"{release.Name} is available.\n\nYou are running {Version}. " +
                           "The installer is downloaded from GitHub Releases and verified against SHA256SUMS.txt before it runs.";
                if (await dashboard.ConfirmAsync("Update available", body, "Install update"))
                    await updates.DownloadAndInstallAsync(release);
            }
            else if (interactive)
            {
                ShowDashboard();
                await dashboard.ShowMessageAsync("Codex Presence", $"You are running the latest version ({Version}).");
            }
        }
        catch (Exception error)
        {
            if (interactive && !exiting) await ShowErrorAsync("Update check failed", error);
        }
        finally
        {
            Interlocked.Exchange(ref updateInProgress, 0);
        }
    }

    private async Task RestartAsync()
    {
        if (exiting) return;
        try
        {
            daemon.InvalidateEndpoint();
            await daemon.RestartAsync();
            await RefreshAsync();
        }
        catch (Exception error)
        {
            await ShowErrorAsync("Could not restart the service", error);
        }
    }

    private async Task ShowErrorAsync(string title, Exception error)
    {
        if (exiting) return;
        ShowDashboard();
        await dashboard.ShowMessageAsync(title, error.Message);
    }

    public async Task ExitAsync()
    {
        if (Interlocked.Exchange(ref exitStarted, 1) != 0) return;
        exiting = true;
        timer.Stop();
        activationWait?.Unregister(null);

        try
        {
            await daemon.StopAsync();
        }
        finally
        {
            try
            {
                CloseWindows();
            }
            finally
            {
                DisposeCore();
                ExitCompleted?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void CloseWindows()
    {
        settingsWindow?.Close();
        settingsWindow = null;
        diagnosticsWindow?.Close();
        diagnosticsWindow = null;
        dashboard.CloseForExit();
    }

    private static bool ShouldCheckForUpdates(int intervalHours)
    {
        try
        {
            if (!File.Exists(AppPaths.StatePath)) return true;
            using var document = JsonDocument.Parse(File.ReadAllText(AppPaths.StatePath));
            var last = document.RootElement.GetProperty("lastUpdateCheck").GetDateTimeOffset();
            return DateTimeOffset.UtcNow - last >= TimeSpan.FromHours(Math.Clamp(intervalHours, 1, 168));
        }
        catch
        {
            return true;
        }
    }

    private static void MarkUpdateCheck()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.StatePath)!);
            File.WriteAllText(
                AppPaths.StatePath,
                JsonSerializer.Serialize(new { lastUpdateCheck = DateTimeOffset.UtcNow }));
        }
        catch
        {
            // A failed timestamp write should not make update checks fail.
        }
    }

    private static string Shorten(string? value, int maximumLength) =>
        string.IsNullOrWhiteSpace(value) ? "No edited file"
        : value.Length <= maximumLength ? value
        : $"…{value[^Math.Max(1, maximumLength - 1)..]}";

    public void Dispose()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        exiting = true;
        timer.Stop();
        activationWait?.Unregister(null);
        try
        {
            CloseWindows();
        }
        finally
        {
            DisposeCore();
        }
        GC.SuppressFinalize(this);
    }

    private void DisposeCore()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        timer.Stop();
        timer.Tick -= OnTimerTick;
        trayIcon.Dispose();
        daemon.Dispose();
        updates.Dispose();
    }
}
