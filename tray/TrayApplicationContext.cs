using System.Reflection;

namespace CodexPresence;

public sealed class TrayApplicationContext : ApplicationContext
{
    // The dashboard only needs a live feed while it is on screen.
    private const int ForegroundPollMs = 2000;
    private const int BackgroundPollMs = 8000;

    private readonly ConfigStore configStore = new();
    private readonly DaemonService daemon;
    private readonly RemoteService remote = new();
    private readonly UpdateService updates = new();
    private readonly NotifyIcon notifyIcon;
    private readonly ContextMenuStrip menu;
    private readonly Dictionary<(UiIcon Icon, int Color), Bitmap> menuIcons = [];
    private readonly ToolStripMenuItem statusItem = new("Starting…") { Enabled = false };
    private readonly ToolStripMenuItem activityItem = new("Waiting for activity") { Enabled = false };
    private readonly ToolStripMenuItem pauseItem = new("Pause presence");
    private readonly System.Windows.Forms.Timer timer = new() { Interval = ForegroundPollMs };
    private readonly DashboardForm dashboard;
    private readonly RegisteredWaitHandle? activationWait;
    private readonly bool showOnStart;

    private HealthSnapshot? latest;
    private bool exiting;
    private bool refreshing;
    private int activationRequested;

    public static string Version => Assembly.GetExecutingAssembly().GetName().Version is { } version
        ? $"{version.Major}.{version.Minor}.{version.Build}"
        : "2.3.2";

    public TrayApplicationContext(bool showOnStart, WaitHandle? activationSignal = null)
    {
        this.showOnStart = showOnStart;
        dashboard = new DashboardForm(Version);
        daemon = new DaemonService(configStore);
        if (!File.Exists(AppPaths.ConfigPath)) configStore.Save(new PresenceConfig());
        dashboard.UpdatePrivacy(configStore.Load().Privacy);

        menu = new ContextMenuStrip
        {
            BackColor = Visuals.Surface,
            ForeColor = Visuals.Text,
            Font = Visuals.Font(9.5f),
            Padding = new Padding(6),
            ShowImageMargin = true,
            ImageScalingSize = new Size(18, 18),
            Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()),
        };
        var open = MenuItem("Open Codex Presence", UiIcon.Brand, Visuals.Text);
        open.Font = Visuals.Font(9.5f, FontStyle.Bold);
        open.Click += (_, _) => ShowDashboard();
        pauseItem.Image = MenuIcon(UiIcon.Pause, Visuals.TextSecondary);
        pauseItem.Click += async (_, _) => await TogglePresenceAsync();
        statusItem.Image = MenuIcon(UiIcon.Info, Visuals.Muted);
        activityItem.Image = MenuIcon(UiIcon.File, Visuals.TextSecondary);
        var settings = MenuItem("Settings…", UiIcon.Settings);
        settings.Click += async (_, _) => await ShowSettingsAsync();
        var doctor = MenuItem("Run diagnostics…", UiIcon.Diagnostics);
        doctor.Click += (_, _) => ShowDiagnostics();
        var update = MenuItem("Check for updates…", UiIcon.Refresh);
        update.Click += async (_, _) => await CheckUpdatesAsync(true);
        var restart = MenuItem("Restart service", UiIcon.Refresh);
        restart.Click += async (_, _) => await RestartAsync();
        var exit = MenuItem("Exit", UiIcon.Exit);
        exit.Click += async (_, _) => await ExitAsync();
        menu.Items.AddRange([open, new ToolStripSeparator(), statusItem, activityItem, new ToolStripSeparator(), pauseItem, settings, doctor, update, restart, new ToolStripSeparator(), exit]);

        notifyIcon = new NotifyIcon
        {
            Icon = Visuals.AppIcon,
            Text = "Codex Presence — starting",
            Visible = true,
            ContextMenuStrip = menu,
        };
        notifyIcon.DoubleClick += (_, _) => ShowDashboard();
        dashboard.PauseRequested += async (_, _) => await TogglePresenceAsync();
        dashboard.SettingsRequested += async (_, _) => await ShowSettingsAsync();
        dashboard.DiagnosticsRequested += (_, _) => ShowDiagnostics();
        dashboard.VisibleChanged += (_, _) => timer.Interval = dashboard.Visible ? ForegroundPollMs : BackgroundPollMs;

        // A second launch of the app raises this handle instead of exiting silently.
        if (activationSignal is not null)
        {
            activationWait = ThreadPool.RegisterWaitForSingleObject(
                activationSignal,
                (_, _) => Interlocked.Exchange(ref activationRequested, 1),
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }

        timer.Tick += async (_, _) => await TickAsync();
        timer.Start();
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await daemon.EnsureRunningAsync();
        }
        catch (Exception error)
        {
            notifyIcon.ShowBalloonTip(5000, "Codex Presence could not start", error.Message, ToolTipIcon.Error);
        }

        await RefreshAsync();
        if (showOnStart) ShowDashboard();

        var config = configStore.Load();
        if (config.Updates.Enabled && ShouldCheckForUpdates(config.Updates.CheckIntervalHours)) await CheckUpdatesAsync(false);
    }

    private async Task TickAsync()
    {
        if (Interlocked.Exchange(ref activationRequested, 0) == 1) ShowDashboard();
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        // The health request can outlive the poll interval; overlapping ticks
        // used to apply their snapshots out of order.
        if (exiting || refreshing) return;
        refreshing = true;
        try
        {
            latest = await daemon.HealthAsync();
        }
        finally
        {
            refreshing = false;
        }

        if (exiting) return;
        dashboard.UpdateSnapshot(latest);

        if (latest is null)
        {
            statusItem.Text = "Service unavailable";
            statusItem.ForeColor = Visuals.Danger;
            statusItem.Image = MenuIcon(UiIcon.Warning, Visuals.Danger);
            activityItem.Text = "Run diagnostics for details";
            notifyIcon.Text = "Codex Presence — offline";
            return;
        }

        statusItem.Text = latest.PresenceEnabled
            ? latest.RpcReady ? "Discord connected" : "Waiting for Discord"
            : "Presence paused";
        statusItem.ForeColor = latest.PresenceEnabled && latest.RpcReady ? Visuals.Success : Visuals.Muted;
        statusItem.Image = MenuIcon(latest.RpcReady && latest.PresenceEnabled ? UiIcon.Check : UiIcon.Info,
            latest.RpcReady && latest.PresenceEnabled ? Visuals.Success : Visuals.Muted);
        activityItem.Text = $"{latest.Project ?? "Project not detected"}  ·  {Shorten(latest.File, 48)}";
        pauseItem.Text = latest.PresenceEnabled ? "Pause presence" : "Resume presence";
        pauseItem.Image = MenuIcon(latest.PresenceEnabled ? UiIcon.Pause : UiIcon.Play, Visuals.TextSecondary);
        notifyIcon.Text = Shorten($"Codex Presence — {latest.Project ?? "waiting"}", 63);
    }

    private async Task TogglePresenceAsync()
    {
        try
        {
            await daemon.ControlAsync(latest?.PresenceEnabled == false ? "resume" : "pause");
            await RefreshAsync();
        }
        catch (Exception error)
        {
            ShowError("Could not change presence state", error);
        }
    }

    private async Task ShowSettingsAsync()
    {
        using var form = new SettingsForm(configStore, remote);
        if (form.ShowDialog(dashboard.Visible ? dashboard : null) != DialogResult.OK || !form.Saved) return;
        dashboard.UpdatePrivacy(configStore.Load().Privacy);
        try
        {
            daemon.InvalidateEndpoint();
            await daemon.RestartAsync();
            await RefreshAsync();
        }
        catch (Exception error)
        {
            ShowError("Settings were saved, but restart failed", error);
        }
    }

    private void ShowDashboard()
    {
        dashboard.UpdateSnapshot(latest);
        if (!dashboard.Visible) dashboard.Show();
        if (dashboard.WindowState == FormWindowState.Minimized) dashboard.WindowState = FormWindowState.Normal;
        dashboard.BringToFront();
        dashboard.Activate();
    }

    private void ShowDiagnostics()
    {
        var service = new DiagnosticsService(daemon, configStore, remote);
        using var form = new DiagnosticsForm(service);
        form.ShowDialog(dashboard.Visible ? dashboard : null);
    }

    private async Task CheckUpdatesAsync(bool interactive)
    {
        try
        {
            var config = configStore.Load();
            var release = await updates.CheckAsync(config.Updates.Repository);
            MarkUpdateCheck();

            if (release is not null && release.Version > updates.CurrentVersion)
            {
                var body = $"{release.Name} is available.\n\nYou are running {Version}. The installer is downloaded from GitHub Releases and verified against SHA256SUMS.txt before it runs.";
                if (ModernDialog.Confirm("Update available", body)) await updates.DownloadAndInstallAsync(release);
            }
            else if (interactive)
            {
                ModernDialog.Show("Codex Presence", $"You are running the latest version ({Version}).", true);
            }
        }
        catch (Exception error)
        {
            if (interactive) ShowError("Update check failed", error);
        }
    }

    private static bool ShouldCheckForUpdates(int intervalHours)
    {
        try
        {
            if (!File.Exists(AppPaths.StatePath)) return true;
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(AppPaths.StatePath));
            var last = document.RootElement.GetProperty("lastUpdateCheck").GetDateTimeOffset();
            return DateTimeOffset.UtcNow - last >= TimeSpan.FromHours(Math.Clamp(intervalHours, 1, 168));
        }
        catch { return true; }
    }

    private static void MarkUpdateCheck()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.StatePath)!);
            File.WriteAllText(AppPaths.StatePath, System.Text.Json.JsonSerializer.Serialize(new { lastUpdateCheck = DateTimeOffset.UtcNow }));
        }
        catch { }
    }

    private async Task RestartAsync()
    {
        try
        {
            daemon.InvalidateEndpoint();
            await daemon.RestartAsync();
            await RefreshAsync();
        }
        catch (Exception error)
        {
            ShowError("Could not restart the service", error);
        }
    }

    private async Task ExitAsync()
    {
        exiting = true;
        timer.Stop();
        await daemon.StopAsync();
        notifyIcon.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            activationWait?.Unregister(null);
            timer.Dispose();
            notifyIcon.Dispose();
            menu.Dispose();
            foreach (var image in menuIcons.Values) image.Dispose();
            dashboard.Dispose();
            daemon.Dispose();
            updates.Dispose();
        }
        base.Dispose(disposing);
    }

    private static string Shorten(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? "No edited file"
        : value.Length <= max ? value
        : $"…{value[^Math.Max(1, max - 1)..]}";

    private static void ShowError(string title, Exception error) => ModernDialog.Show(title, error.Message, false);

    private ToolStripMenuItem MenuItem(string text, UiIcon icon, Color? color = null) => new(text)
    {
        Image = MenuIcon(icon, color ?? Visuals.TextSecondary),
        ImageScaling = ToolStripItemImageScaling.SizeToFit,
    };

    private Bitmap MenuIcon(UiIcon icon, Color color)
    {
        var key = (icon, color.ToArgb());
        if (menuIcons.TryGetValue(key, out var bitmap)) return bitmap;
        bitmap = UiIcons.RenderBitmap(icon, 18, color);
        menuIcons[key] = bitmap;
        return bitmap;
    }

    private sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Visuals.Surface;
        public override Color MenuItemSelected => Visuals.SurfaceRaised;
        public override Color MenuItemBorder => Visuals.Accent;
        public override Color ImageMarginGradientBegin => Visuals.Surface;
        public override Color ImageMarginGradientMiddle => Visuals.Surface;
        public override Color ImageMarginGradientEnd => Visuals.Surface;
        public override Color SeparatorDark => Visuals.SurfaceRaised;
        public override Color SeparatorLight => Visuals.SurfaceRaised;
    }
}
