using System.Diagnostics;

namespace CodexPresence;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly ConfigStore configStore = new();
    private readonly DaemonService daemon;
    private readonly RemoteService remote = new();
    private readonly UpdateService updates = new();
    private readonly NotifyIcon notifyIcon;
    private readonly ToolStripMenuItem statusItem = new("Starting…") { Enabled = false };
    private readonly ToolStripMenuItem activityItem = new("Waiting for activity") { Enabled = false };
    private readonly ToolStripMenuItem pauseItem = new("Pause presence");
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    private readonly DashboardForm dashboard = new();
    private HealthSnapshot? latest;
    private bool exiting;

    private readonly bool showOnStart;

    public TrayApplicationContext(bool showOnStart)
    {
        this.showOnStart = showOnStart;
        daemon = new DaemonService(configStore);
        if (!File.Exists(AppPaths.ConfigPath)) configStore.Save(new PresenceConfig());
        var menu = new ContextMenuStrip
        {
            BackColor = Visuals.Surface,
            ForeColor = Visuals.Text,
            Font = Visuals.Font(9.5f),
            Padding = new Padding(6),
            ShowImageMargin = false,
            Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()),
        };
        var open = new ToolStripMenuItem("Open Codex Presence") { Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        open.Click += (_, _) => ShowDashboard();
        pauseItem.Click += async (_, _) => await TogglePresenceAsync();
        var settings = new ToolStripMenuItem("Settings…"); settings.Click += async (_, _) => await ShowSettingsAsync();
        var doctor = new ToolStripMenuItem("Run diagnostics…"); doctor.Click += (_, _) => ShowDiagnostics();
        var update = new ToolStripMenuItem("Check for updates…"); update.Click += async (_, _) => await CheckUpdatesAsync(true);
        var restart = new ToolStripMenuItem("Restart service"); restart.Click += async (_, _) => await RestartAsync();
        var exit = new ToolStripMenuItem("Exit"); exit.Click += async (_, _) => await ExitAsync();
        menu.Items.AddRange([open, new ToolStripSeparator(), statusItem, activityItem, new ToolStripSeparator(), pauseItem, settings, doctor, update, restart, new ToolStripSeparator(), exit]);

        notifyIcon = new NotifyIcon
        {
            Icon = Visuals.CreateIcon(),
            Text = "Codex Presence — starting",
            Visible = true,
            ContextMenuStrip = menu,
        };
        notifyIcon.DoubleClick += (_, _) => ShowDashboard();
        dashboard.PauseRequested += async (_, _) => await TogglePresenceAsync();
        dashboard.SettingsRequested += async (_, _) => await ShowSettingsAsync();
        dashboard.DiagnosticsRequested += (_, _) => ShowDiagnostics();
        timer.Tick += async (_, _) => await RefreshAsync();
        timer.Start();
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try { await daemon.EnsureRunningAsync(); }
        catch (Exception error)
        {
            notifyIcon.ShowBalloonTip(5000, "Codex Presence could not start", error.Message, ToolTipIcon.Error);
        }
        await RefreshAsync();
        if (showOnStart) ShowDashboard();
        var config = configStore.Load();
        if (config.Updates.Enabled && ShouldCheckForUpdates(config.Updates.CheckIntervalHours)) await CheckUpdatesAsync(false);
    }

    private async Task RefreshAsync()
    {
        if (exiting) return;
        latest = await daemon.HealthAsync();
        dashboard.UpdateSnapshot(latest);
        if (latest is null)
        {
            statusItem.Text = "● Service unavailable";
            statusItem.ForeColor = Visuals.Danger;
            activityItem.Text = "Run diagnostics for details";
            notifyIcon.Text = "Codex Presence — offline";
            return;
        }
        statusItem.Text = latest.RpcReady ? "● Discord connected" : "● Waiting for Discord";
        statusItem.ForeColor = latest.RpcReady ? Visuals.Success : Visuals.Muted;
        activityItem.Text = $"{latest.Project ?? "Waiting"}  ·  {Shorten(latest.File, 48)}";
        pauseItem.Text = latest.PresenceEnabled ? "Pause presence" : "Resume presence";
        notifyIcon.Text = Shorten($"Codex Presence — {latest.Project}", 63);
    }

    private async Task TogglePresenceAsync()
    {
        try
        {
            await daemon.ControlAsync(latest?.PresenceEnabled == false ? "resume" : "pause");
            await RefreshAsync();
        }
        catch (Exception error) { ShowError("Could not change presence state", error); }
    }

    private async Task ShowSettingsAsync()
    {
        using var form = new SettingsForm(configStore, remote);
        if (form.ShowDialog() == DialogResult.OK && form.Saved)
        {
            try { await daemon.RestartAsync(); await RefreshAsync(); }
            catch (Exception error) { ShowError("Settings were saved, but restart failed", error); }
        }
    }

    private void ShowDashboard()
    {
        dashboard.UpdateSnapshot(latest);
        if (!dashboard.Visible) dashboard.Show();
        if (dashboard.WindowState == FormWindowState.Minimized) dashboard.WindowState = FormWindowState.Normal;
        dashboard.Activate();
    }

    private void ShowDiagnostics()
    {
        var service = new DiagnosticsService(daemon, configStore, remote);
        using var form = new DiagnosticsForm(service);
        form.ShowDialog();
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
                var answer = MessageBox.Show($"{release.Name} is available. Download and install it now?", "Codex Presence update", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer == DialogResult.Yes) await updates.DownloadAndInstallAsync(release);
            }
            else if (interactive) MessageBox.Show("You are running the latest version.", "Codex Presence", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception error) { if (interactive) ShowError("Update check failed", error); }
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
        try { await daemon.RestartAsync(); await RefreshAsync(); }
        catch (Exception error) { ShowError("Could not restart the service", error); }
    }

    private async Task ExitAsync()
    {
        exiting = true;
        timer.Stop();
        await daemon.StopAsync();
        notifyIcon.Visible = false;
        dashboard.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            notifyIcon.Dispose();
            dashboard.Dispose();
            daemon.Dispose();
            updates.Dispose();
        }
        base.Dispose(disposing);
    }

    private static string Shorten(string? value, int max) => string.IsNullOrWhiteSpace(value) ? "No edited file" : value.Length <= max ? value : $"…{value[^Math.Max(1, max - 1)..]}";
    private static void ShowError(string title, Exception error) => MessageBox.Show(error.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);

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
