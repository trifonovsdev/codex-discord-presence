namespace CodexPresence;

public sealed class DashboardForm : ModernForm
{
    private readonly StatusPill connection = new() { Text = "Connecting" };
    private readonly Label project = Visuals.Heading("Waiting for Codex", 20);
    private readonly Label file = Visuals.Label("No activity yet", 9.5f, true);
    private readonly Label elapsed = Visuals.Label("00:00:00", 10, false, FontStyle.Bold);
    private readonly Label source = Visuals.Label("Session monitor", 9, false, FontStyle.Bold);
    private readonly Label workspace = Visuals.Label("Local desktop", 9, false, FontStyle.Bold);
    private readonly ModernButton pause = Visuals.Button("Pause presence", ButtonKind.Primary, UiIcon.Pause);
    private readonly ModernButton copyPath = Visuals.Button("Copy path", ButtonKind.Ghost, UiIcon.Copy);
    private readonly RoundedPanel alert = new() { Radius = 10, BackColor = Visuals.DangerSurface, BorderColor = Visuals.DangerSurface, Visible = false };
    private readonly Label alertText = Visuals.Label("", 8.5f);
    private readonly DiscordCardPreview preview = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer ticker = new() { Interval = 1000 };
    private readonly ToolTip tooltip = new();

    private DateTimeOffset? startedAt;
    private string? pathToCopy;

    public event EventHandler? PauseRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? DiagnosticsRequested;

    public DashboardForm(string version) : base("Codex Presence", new Size(760, 540), resizable: true)
    {
        MinimumSize = new Size(680, 500);

        var header = new Panel { Dock = DockStyle.Top, Height = 88, BackColor = Visuals.Background };
        var heading = Visuals.Heading("Presence", 21);
        heading.Location = new Point(28, 21);
        var subtitle = Visuals.Label("The selected Codex task and its live Discord card", 9.25f, true);
        subtitle.Location = new Point(29, 54);
        connection.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        header.Controls.AddRange([heading, subtitle, connection]);
        header.Resize += (_, _) => connection.Location = new Point(header.Width - connection.Width - 28, 28);

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Visuals.Background, Padding = new Padding(28, 0, 28, 18) };
        var columns = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
        };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));

        var activity = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            Radius = 16,
            BackColor = Visuals.Surface,
            BorderColor = Visuals.Border,
            Margin = new Padding(0, 0, 7, 0),
        };
        preview.Margin = new Padding(7, 0, 0, 0);

        var eyebrow = Visuals.Eyebrow("Current activity");
        eyebrow.Location = new Point(20, 18);
        project.SetBounds(19, 43, 280, 32);
        project.AutoSize = false;
        project.AutoEllipsis = true;
        project.TextAlign = ContentAlignment.MiddleLeft;
        file.SetBounds(20, 79, 150, 24);
        file.AutoSize = false;
        file.AutoEllipsis = true;
        file.TextAlign = ContentAlignment.MiddleLeft;
        file.AccessibleName = "Current file";

        copyPath.SetBounds(0, 70, 116, 34);
        copyPath.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        copyPath.Enabled = false;
        copyPath.AccessibleDescription = "Copies the repository-relative path";
        copyPath.Click += (_, _) => CopyCurrentPath();
        tooltip.SetToolTip(copyPath, "Copy repository-relative path");

        var divider = new Panel { Location = new Point(20, 124), Height = 1, BackColor = Visuals.BorderSoft };
        var stats = new TableLayoutPanel
        {
            Location = new Point(20, 142),
            Height = 56,
            ColumnCount = 3,
            RowCount = 2,
            BackColor = Color.Transparent,
        };
        stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31));
        stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        stats.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        stats.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        stats.Controls.Add(Visuals.Eyebrow("Source"), 0, 0);
        stats.Controls.Add(Visuals.Eyebrow("Workspace"), 1, 0);
        stats.Controls.Add(Visuals.Eyebrow("Session"), 2, 0);
        foreach (var value in new[] { source, workspace, elapsed })
        {
            value.AutoSize = false;
            value.AutoEllipsis = true;
            value.Dock = DockStyle.Fill;
            value.TextAlign = ContentAlignment.MiddleLeft;
        }
        stats.Controls.Add(source, 0, 1);
        stats.Controls.Add(workspace, 1, 1);
        elapsed.Font = Visuals.MonoFont(9.5f, FontStyle.Bold);
        stats.Controls.Add(elapsed, 2, 1);

        alert.Height = 42;
        alertText.ForeColor = Visuals.Danger;
        alertText.Location = new Point(12, 12);
        alertText.AutoEllipsis = true;
        alert.Controls.Add(alertText);

        activity.Controls.AddRange([eyebrow, project, file, copyPath, divider, stats, alert]);
        activity.Resize += (_, _) =>
        {
            var inner = activity.Width - 40;
            divider.Width = inner;
            stats.Width = inner;
            copyPath.Left = activity.Width - copyPath.Width - 16;
            alert.Width = inner;
            alert.Location = new Point(20, Math.Max(stats.Bottom + 14, activity.Height - alert.Height - 16));
            project.Width = Math.Max(140, inner);
            file.Width = Math.Max(80, inner - copyPath.Width - 14);
            alertText.MaximumSize = new Size(Math.Max(80, inner - 24), 0);
        };

        columns.Controls.Add(activity, 0, 0);
        columns.Controls.Add(preview, 1, 0);
        body.Controls.Add(columns);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 78, BackColor = Visuals.Canvas };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Visuals.BorderSoft);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };

        pause.SetBounds(28, 18, 176, 42);
        pause.Click += (_, _) => PauseRequested?.Invoke(this, EventArgs.Empty);
        var settings = Visuals.Button("Settings", ButtonKind.Secondary, UiIcon.Settings);
        settings.SetBounds(216, 18, 142, 42);
        settings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        var doctor = Visuals.Button("Doctor", ButtonKind.Ghost, UiIcon.Diagnostics);
        doctor.SetBounds(368, 18, 122, 42);
        doctor.Click += (_, _) => DiagnosticsRequested?.Invoke(this, EventArgs.Empty);
        var versionLabel = Visuals.Label(version, 8.5f, true);
        versionLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        footer.Controls.AddRange([pause, settings, doctor, versionLabel]);
        footer.Resize += (_, _) => versionLabel.Location = new Point(footer.Width - versionLabel.Width - 24, 32);

        ContentHost.Controls.Add(body);
        ContentHost.Controls.Add(footer);
        ContentHost.Controls.Add(header);

        ticker.Tick += (_, _) => RenderElapsed();
        VisibleChanged += (_, _) => { if (Visible) ticker.Start(); else ticker.Stop(); };
        FormClosing += (_, eventArgs) =>
        {
            if (eventArgs.CloseReason != CloseReason.UserClosing) return;
            eventArgs.Cancel = true;
            Hide();
        };
    }

    public void UpdateSnapshot(HealthSnapshot? health)
    {
        if (health is null)
        {
            startedAt = null;
            pathToCopy = null;
            SetConnection("Service offline", Visuals.Danger, Visuals.DangerSurface, live: false);
            project.Text = "Service not connected";
            file.Text = "Run Doctor to inspect the local service.";
            elapsed.Text = "--:--:--";
            source.Text = "Unavailable";
            workspace.Text = "Unknown";
            pause.Enabled = false;
            copyPath.Enabled = false;
            preview.ProjectName = null;
            preview.TaskTitle = null;
            preview.FileName = null;
            preview.Connected = false;
            preview.Published = false;
            preview.HasTimestamp = false;
            preview.Elapsed = elapsed.Text;
            ShowAlert(null);
            return;
        }

        var live = health.PresenceEnabled && health.RpcReady && health.CodexRunning;
        if (!health.PresenceEnabled) SetConnection("Presence paused", Visuals.Muted, Visuals.SurfaceRaised, live: false);
        else if (!health.CodexRunning) SetConnection("Waiting for Codex", Visuals.Muted, Visuals.SurfaceRaised, live: false);
        else if (health.RpcReady) SetConnection("Live on Discord", Visuals.Success, Visuals.SuccessSurface, live: true);
        else SetConnection("Waiting for Discord", Visuals.Muted, Visuals.SurfaceRaised, live: false);

        var hasProject = !string.IsNullOrWhiteSpace(health.Project);
        project.Text = hasProject ? health.Project! : "Project not detected";
        file.Text = hasProject
            ? string.IsNullOrWhiteSpace(health.File) ? "No edited file yet" : health.File
            : health.CodexRunning ? "This task has no detectable workspace yet." : "Open Codex to start sharing activity.";
        pathToCopy = string.IsNullOrWhiteSpace(health.File) ? null : health.File;
        copyPath.Enabled = pathToCopy is not null;
        source.Text = FriendlySource(health.Source);
        workspace.Text = health.SelectedRemote is { Length: > 0 } remote ? remote : "Local desktop";
        startedAt = health.CodexStartedAt;
        pause.Text = health.PresenceEnabled ? "Pause presence" : "Resume presence";
        pause.Icon = health.PresenceEnabled ? UiIcon.Pause : UiIcon.Play;
        pause.Enabled = true;

        preview.ProjectName = health.Project;
        preview.TaskTitle = health.Task;
        preview.ShowTaskTitle = health.TaskTitleShared;
        preview.FileName = health.File;
        preview.Language = health.Language ?? "en";
        preview.Connected = live;
        preview.Published = live;
        preview.HasTimestamp = health.CodexStartedAt is not null;
        RenderElapsed();
        ShowAlert(health.LastRemoteError);
    }

    public void UpdatePrivacy(PrivacyConfig privacy)
    {
        preview.ShowProject = privacy.ShowProject;
        preview.ShowTaskTitle = privacy.ShowTaskTitle;
        preview.ShowFile = privacy.ShowFile;
        preview.ShowTimer = privacy.ShowTimer;
        preview.FileMode = privacy.FileMode;
    }

    private void SetConnection(string text, Color dot, Color fill, bool live)
    {
        connection.Text = text;
        connection.DotColor = dot;
        connection.FillColor = fill;
        connection.IsLive = live;
    }

    private void ShowAlert(string? message)
    {
        var visible = !string.IsNullOrWhiteSpace(message);
        alert.Visible = visible;
        alertText.Text = visible ? $"SSH workspace: {message}" : string.Empty;
        AccessibleDescription = visible ? alertText.Text : null;
        if (visible && IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.DescriptionChange, -1);
    }

    private void RenderElapsed()
    {
        elapsed.Text = startedAt is { } started ? FormatElapsed(DateTimeOffset.Now - started) : "Codex is closed";
        preview.Elapsed = elapsed.Text;
    }

    private void CopyCurrentPath()
    {
        if (string.IsNullOrWhiteSpace(pathToCopy)) return;
        try
        {
            Clipboard.SetText(pathToCopy);
            tooltip.Show("Path copied", copyPath, copyPath.Width / 2, copyPath.Height + 6, 1200);
        }
        catch (Exception error)
        {
            ModernDialog.Show(this, "Could not copy the path", error.Message, false);
        }
    }

    private static string FriendlySource(string? value) => value switch
    {
        "desktop-route+remote-session" => "Remote task",
        "desktop-route+session" => "Selected task",
        "desktop-route" => "Desktop route",
        "hook" => "Live hook",
        _ => "Session monitor",
    };

    private static string FormatElapsed(TimeSpan value) => value.TotalHours >= 24
        ? $"{(int)value.TotalDays}d {value:hh\\:mm\\:ss}"
        : value.ToString("hh\\:mm\\:ss");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ticker.Dispose();
            tooltip.Dispose();
        }
        base.Dispose(disposing);
    }
}
