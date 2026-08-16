namespace CodexPresence;

/// <summary>
/// The dashboard is a live controller, not a collection of metrics. Its
/// composition follows the thing this product actually does: selected Codex
/// activity enters on the left, crosses the local relay, and becomes the
/// Discord card on the right.
/// </summary>
public sealed class DashboardForm : ModernForm
{
    private readonly StatusPill connection = new()
    {
        Text = "Connecting",
        FillColor = Visuals.Canvas,
        DotColor = Visuals.Muted,
    };
    private readonly AnimatedText project = new()
    {
        Text = "Waiting for Codex",
        Font = Visuals.DisplayFont(27, FontStyle.Bold),
        TextColor = Visuals.Text,
        Height = 46,
    };
    private readonly AnimatedText file = new()
    {
        Text = "No activity yet",
        Font = Visuals.MonoFont(9.5f),
        TextColor = Visuals.TextSecondary,
        Height = 28,
        TravelDp = 4,
    };
    private readonly Label activityContext = Visuals.Label("Selected task · Local desktop", 9, true);
    private readonly Label footerContext = Visuals.Label("Session monitor · Local desktop", 8.75f, true);
    private readonly Label elapsed = Visuals.Label("00:00:00", 9.25f, false, FontStyle.Bold);
    private readonly Label versionLabel;
    private readonly Label privacyProject = PrivacyValue("Shared");
    private readonly Label privacyTask = PrivacyValue("Private");
    private readonly Label privacyFile = PrivacyValue("Relative path");
    private readonly Label privacyTimer = PrivacyValue("Visible");
    private readonly ModernButton pause = Visuals.Button("Pause presence", ButtonKind.Primary, UiIcon.Pause);
    private readonly ModernButton copyPath = Visuals.Button("", ButtonKind.Ghost, UiIcon.Copy);
    private readonly ModernButton settings = Visuals.Button("", ButtonKind.Ghost, UiIcon.Settings);
    private readonly ModernButton doctor = Visuals.Button("", ButtonKind.Ghost, UiIcon.Diagnostics);
    private readonly SignalRelayControl relay = new() { Status = SignalRelayStatus.Offline };
    private readonly RoundedPanel alert = new()
    {
        Radius = 5,
        BorderWidth = 0,
        BackColor = Visuals.DangerSurface,
        Visible = false,
    };
    private readonly IconView alertIcon = new(UiIcon.Warning) { IconColor = Visuals.Danger, Size = new Size(18, 18) };
    private readonly Label alertText = Visuals.Label("", 8.5f);
    private readonly DiscordCardPreview preview = new()
    {
        Height = 190,
        Radius = 0,
        BorderWidth = 0,
        BackColor = Visuals.Canvas,
        Published = false,
    };
    private readonly System.Windows.Forms.Timer ticker = new() { Interval = 1000 };
    private readonly ToolTip tooltip = new();
    private readonly List<Panel> privacyRows = [];

    private DateTimeOffset? startedAt;
    private string? pathToCopy;
    private string? lastAlertMessage;
    private string? lastAnnouncedAlert;
    private DateTimeOffset lastRelayAnimationAt;
    private bool? lastPublishedState;

    public event EventHandler? PauseRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? DiagnosticsRequested;

    public DashboardForm(string version) : base("Codex Presence", new Size(920, 560), resizable: true)
    {
        MinimumSize = new Size(780, 548);
        versionLabel = Visuals.Label(version, 8, true);
        versionLabel.Font = Visuals.MonoFont(8);

        ConfigureTitleCommands();

        var footer = BuildFooter();
        var split = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Visuals.Background,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        split.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        split.Controls.Add(BuildActivityPane(), 0, 0);
        split.Controls.Add(BuildOutputPane(), 1, 0);
        ContentHost.Controls.Add(split);
        ContentHost.Controls.Add(footer);

        ticker.Tick += (_, _) => RenderElapsed();
        VisibleChanged += (_, _) => { if (Visible) ticker.Start(); else ticker.Stop(); };
        Shown += (_, _) => AnnounceAlertIfNeeded();
        FormClosing += (_, eventArgs) =>
        {
            if (eventArgs.CloseReason != CloseReason.UserClosing) return;
            eventArgs.Cancel = true;
            Hide();
        };
    }

    private void ConfigureTitleCommands()
    {
        connection.Height = 28;
        connection.IsLive = false;

        foreach (var command in new[] { doctor, settings })
        {
            command.Size = new Size(36, 34);
            command.Radius = 5;
            command.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        }
        doctor.AccessibleName = "Run diagnostics";
        doctor.AccessibleDescription = "Checks the local service, Discord, Codex hooks, and SSH workspaces";
        settings.AccessibleName = "Open settings";
        doctor.Click += (_, _) => DiagnosticsRequested?.Invoke(this, EventArgs.Empty);
        settings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        tooltip.SetToolTip(doctor, "Doctor");
        tooltip.SetToolTip(settings, "Settings");

        TitleBar.Controls.AddRange([connection, doctor, settings, versionLabel]);
        TitleBar.Resize += (_, _) => LayoutTitleCommands();
        LayoutTitleCommands();
    }

    private Control BuildActivityPane()
    {
        var pane = new Panel { Dock = DockStyle.Fill, BackColor = Visuals.Background, Margin = Padding.Empty };
        activityContext.AutoEllipsis = true;
        activityContext.AutoSize = false;
        project.AccessibleDescription = "Current project";
        file.AccessibleDescription = "Current file";

        copyPath.Size = new Size(34, 34);
        copyPath.Radius = 5;
        copyPath.Enabled = false;
        copyPath.AccessibleName = "Copy current path";
        copyPath.AccessibleDescription = "Copies the repository-relative file path";
        copyPath.Click += (_, _) => CopyCurrentPath();
        tooltip.SetToolTip(copyPath, "Copy repository-relative path");

        alertIcon.Location = new Point(12, 12);
        alertText.ForeColor = Visuals.Danger;
        alertText.AutoSize = false;
        alertText.AutoEllipsis = true;
        alert.AccessibleRole = AccessibleRole.Alert;
        alert.AccessibleName = "Presence warning";
        foreach (var target in new Control[] { alert, alertIcon, alertText })
        {
            target.Cursor = Cursors.Hand;
            target.Click += (_, _) => DiagnosticsRequested?.Invoke(this, EventArgs.Empty);
        }
        alert.Controls.AddRange([alertIcon, alertText]);

        pane.Controls.AddRange([activityContext, project, file, copyPath, alert, relay]);
        pane.Resize += (_, _) => LayoutActivityPane(pane);
        return pane;
    }

    private Control BuildOutputPane()
    {
        var pane = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Visuals.Canvas,
            Margin = Padding.Empty,
            Padding = new Padding(24, 20, 24, 20),
        };
        pane.Paint += (_, e) =>
        {
            using var rule = new Pen(Visuals.BorderSoft);
            e.Graphics.DrawLine(rule, 0, 0, 0, pane.Height);
        };

        var privacyHeading = Visuals.Label("What Discord can see", 9.25f, false, FontStyle.Bold);
        privacyHeading.AutoSize = false;
        privacyHeading.Height = 24;
        privacyHeading.AccessibleRole = AccessibleRole.StaticText;
        privacyHeading.AccessibleName = "Discord visibility";

        privacyRows.Add(PrivacyRow("Project", privacyProject));
        privacyRows.Add(PrivacyRow("Task title", privacyTask));
        privacyRows.Add(PrivacyRow("File", privacyFile));
        privacyRows.Add(PrivacyRow("Timer", privacyTimer));

        pane.Controls.Add(preview);
        pane.Controls.Add(privacyHeading);
        foreach (var row in privacyRows) pane.Controls.Add(row);
        pane.Resize += (_, _) => LayoutOutputPane(pane, privacyHeading);
        return pane;
    }

    private Panel BuildFooter()
    {
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = Visuals.Canvas };
        footer.Paint += (_, e) =>
        {
            using var rule = new Pen(Visuals.BorderSoft);
            e.Graphics.DrawLine(rule, 0, 0, footer.Width, 0);
        };

        footerContext.AutoSize = false;
        footerContext.AutoEllipsis = true;
        footerContext.TextAlign = ContentAlignment.MiddleLeft;
        elapsed.Font = Visuals.MonoFont(9.25f, FontStyle.Bold);
        elapsed.AutoSize = false;
        elapsed.TextAlign = ContentAlignment.MiddleRight;
        elapsed.AccessibleDescription = "Codex session duration";

        pause.Height = 38;
        pause.Width = 158;
        pause.Radius = 5;
        pause.Click += (_, _) => PauseRequested?.Invoke(this, EventArgs.Empty);
        footer.Controls.AddRange([footerContext, elapsed, pause]);
        footer.Resize += (_, _) =>
        {
            pause.Location = new Point(footer.Width - pause.Width - footer.Dp(22), footer.Dp(13));
            elapsed.SetBounds(pause.Left - footer.Dp(126), footer.Dp(13), footer.Dp(110), footer.Dp(38));
            footerContext.SetBounds(footer.Dp(32), footer.Dp(13), Math.Max(footer.Dp(160), elapsed.Left - footer.Dp(54)), footer.Dp(38));
        };
        return footer;
    }

    private static Panel PrivacyRow(string name, Label value)
    {
        var row = new Panel { Height = 38, BackColor = Visuals.Canvas, Margin = Padding.Empty };
        var label = Visuals.Label(name, 8.5f, true);
        label.AutoSize = false;
        label.TextAlign = ContentAlignment.MiddleLeft;
        value.AutoSize = false;
        value.TextAlign = ContentAlignment.MiddleRight;
        value.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        row.Controls.AddRange([label, value]);
        row.Paint += (_, e) =>
        {
            using var rule = new Pen(Visuals.BorderSoft);
            e.Graphics.DrawLine(rule, 0, row.Height - 1, row.Width, row.Height - 1);
        };
        row.Resize += (_, _) =>
        {
            label.SetBounds(0, 0, Math.Max(row.Dp(70), row.Width / 2), row.Height - 1);
            value.SetBounds(row.Width / 2, 0, row.Width - row.Width / 2, row.Height - 1);
        };
        return row;
    }

    private static Label PrivacyValue(string text) => Visuals.Label(text, 8.5f, false, FontStyle.Bold);

    private void LayoutTitleCommands()
    {
        if (TitleBar.Width <= 0) return;
        var captionReserve = TitleBar.Dp(146);
        settings.Location = new Point(TitleBar.Width - captionReserve - settings.Width - TitleBar.Dp(8), TitleBar.Dp(7));
        doctor.Location = new Point(settings.Left - doctor.Width - TitleBar.Dp(4), TitleBar.Dp(7));
        connection.Location = new Point(Math.Max(TitleBar.Dp(190), doctor.Left - connection.Width - TitleBar.Dp(12)), TitleBar.Dp(10));
        versionLabel.Location = new Point(TitleBar.Dp(166), TitleBar.Dp(16));
    }

    private void LayoutActivityPane(Control pane)
    {
        var left = pane.Dp(38);
        var right = pane.Dp(34);
        var innerWidth = Math.Max(pane.Dp(260), pane.Width - left - right);
        activityContext.SetBounds(left, pane.Dp(30), innerWidth, pane.Dp(24));
        project.SetBounds(left, pane.Dp(57), innerWidth, pane.Dp(48));
        copyPath.Location = new Point(pane.Width - right - copyPath.Width, pane.Dp(105));
        file.SetBounds(left, pane.Dp(108), Math.Max(pane.Dp(160), copyPath.Left - left - pane.Dp(10)), pane.Dp(30));

        alert.SetBounds(left, pane.Dp(150), innerWidth, pane.Dp(42));
        alertText.SetBounds(pane.Dp(42), 0, Math.Max(pane.Dp(100), alert.Width - pane.Dp(54)), alert.Height);

        var relayHeight = Math.Clamp(pane.Dp(118), pane.Dp(92), Math.Max(pane.Dp(92), pane.Height - pane.Dp(190)));
        relay.SetBounds(pane.Dp(22), pane.Height - relayHeight - pane.Dp(18), Math.Max(pane.Dp(360), pane.Width - pane.Dp(44)), relayHeight);
    }

    private void LayoutOutputPane(Control pane, Control privacyHeading)
    {
        var left = pane.Padding.Left;
        var width = Math.Max(pane.Dp(220), pane.ClientSize.Width - pane.Padding.Horizontal);
        preview.SetBounds(left, pane.Padding.Top, width, pane.Dp(190));
        privacyHeading.SetBounds(left, preview.Bottom + pane.Dp(18), width, pane.Dp(24));
        var top = privacyHeading.Bottom + pane.Dp(5);
        foreach (var row in privacyRows)
        {
            row.SetBounds(left, top, width, pane.Dp(38));
            top += row.Height;
        }
    }

    public void UpdateSnapshot(HealthSnapshot? health)
    {
        if (health is null)
        {
            startedAt = null;
            pathToCopy = null;
            SetConnection("Service offline", Visuals.Danger, live: false);
            activityContext.Text = "Local service unavailable";
            footerContext.Text = "Run Doctor to inspect the local service";
            project.Text = "Service not connected";
            file.Text = "No activity is being published";
            elapsed.Text = "--:--:--";
            pause.Enabled = false;
            copyPath.Enabled = false;
            relay.Status = SignalRelayStatus.Offline;
            lastPublishedState = false;
            preview.ProjectName = null;
            preview.TaskTitle = null;
            preview.FileName = null;
            preview.PublishError = null;
            preview.Connected = false;
            preview.Published = false;
            preview.HasTimestamp = false;
            preview.Elapsed = elapsed.Text;
            ShowAlert(null);
            return;
        }

        var live = health.PresenceEnabled && health.RpcReady && health.CodexRunning;
        var published = live && health.RpcPublished;
        var publishFailed = live && !string.IsNullOrWhiteSpace(health.RpcError);
        if (!health.PresenceEnabled) SetConnection("Presence paused", Visuals.Muted, live: false);
        else if (!health.CodexRunning) SetConnection("Waiting for Codex", Visuals.Muted, live: false);
        else if (publishFailed) SetConnection("Discord rejected update", Visuals.Danger, live: false);
        else if (published) SetConnection("Live on Discord", Visuals.Success, live: true);
        else if (health.RpcReady) SetConnection("Publishing to Discord", Visuals.Muted, live: false);
        else SetConnection("Waiting for Discord", Visuals.Muted, live: false);

        var source = FriendlySource(health.Source);
        var workspace = health.SelectedRemote is { Length: > 0 } remote ? remote : "Local desktop";
        activityContext.Text = $"{source} · {workspace}";
        footerContext.Text = "Local relay · Prompts stay private";

        var hasProject = !string.IsNullOrWhiteSpace(health.Project);
        project.Text = hasProject ? health.Project! : health.CodexRunning ? "Working in Codex" : "Waiting for Codex";
        file.Text = hasProject
            ? string.IsNullOrWhiteSpace(health.File) ? "No edited file yet" : health.File
            : health.CodexRunning ? "No detectable workspace for this task" : "Open a task to start sharing activity";
        pathToCopy = string.IsNullOrWhiteSpace(health.File) ? null : health.File;
        copyPath.Enabled = pathToCopy is not null;
        startedAt = health.CodexStartedAt;
        pause.Text = health.PresenceEnabled ? "Pause presence" : "Resume presence";
        pause.Icon = health.PresenceEnabled ? UiIcon.Pause : UiIcon.Play;
        pause.Enabled = true;

        relay.Status = !health.PresenceEnabled
            ? SignalRelayStatus.Paused
            : !live ? SignalRelayStatus.Offline
            : publishFailed ? SignalRelayStatus.Failed
            : published ? SignalRelayStatus.Live : SignalRelayStatus.Pending;

        preview.ProjectName = health.Project;
        preview.TaskTitle = health.Task;
        preview.ShowTaskTitle = health.TaskTitleShared;
        preview.FileName = health.File;
        preview.PublishError = health.RpcError;
        preview.Language = health.Language ?? "en";
        preview.Connected = live;
        preview.Published = published;
        preview.HasTimestamp = health.CodexStartedAt is not null;
        RenderElapsed();
        ShowAlert(!string.IsNullOrWhiteSpace(health.RpcError)
            ? $"Discord: {health.RpcError}"
            : string.IsNullOrWhiteSpace(health.LastRemoteError) ? null : $"SSH workspace: {health.LastRemoteError}");

        if (lastPublishedState is false && published)
        {
            var now = DateTimeOffset.UtcNow;
            if (Visible && now - lastRelayAnimationAt >= TimeSpan.FromSeconds(8))
            {
                lastRelayAnimationAt = now;
                relay.Publish();
            }
        }
        lastPublishedState = published;
    }

    public void UpdatePrivacy(PrivacyConfig privacy)
    {
        preview.ShowProject = privacy.ShowProject;
        preview.ShowTaskTitle = privacy.ShowTaskTitle;
        preview.ShowFile = privacy.ShowFile;
        preview.ShowTimer = privacy.ShowTimer;
        preview.FileMode = privacy.FileMode;
        privacyProject.Text = privacy.ShowProject ? "Shared" : "Hidden";
        privacyTask.Text = privacy.ShowTaskTitle ? "Shared" : "Private";
        privacyFile.Text = !privacy.ShowFile
            ? "Hidden"
            : string.Equals(privacy.FileMode, "name", StringComparison.OrdinalIgnoreCase) ? "Filename" : "Relative path";
        privacyTimer.Text = privacy.ShowTimer ? "Visible" : "Hidden";
    }

    private void SetConnection(string text, Color dot, bool live)
    {
        connection.Text = text;
        connection.DotColor = dot;
        connection.FillColor = Visuals.Canvas;
        connection.IsLive = live;
        LayoutTitleCommands();
    }

    private void ShowAlert(string? message)
    {
        if (!string.Equals(lastAlertMessage, message, StringComparison.Ordinal))
        {
            lastAlertMessage = message;
            var visible = !string.IsNullOrWhiteSpace(message);
            alert.Visible = visible;
            alertText.Text = visible ? message : string.Empty;
            alert.AccessibleDescription = alertText.Text;
            tooltip.SetToolTip(alert, alertText.Text);
            tooltip.SetToolTip(alertIcon, alertText.Text);
            tooltip.SetToolTip(alertText, visible ? $"{alertText.Text}\nClick to open Doctor." : string.Empty);
            AccessibleDescription = visible ? alertText.Text : null;
        }
        AnnounceAlertIfNeeded();
    }

    private void AnnounceAlertIfNeeded()
    {
        if (!Visible || !IsHandleCreated || string.Equals(lastAnnouncedAlert, lastAlertMessage, StringComparison.Ordinal)) return;
        lastAnnouncedAlert = lastAlertMessage;
        AccessibilityNotifyClients(AccessibleEvents.DescriptionChange, -1);
    }

    private void RenderElapsed()
    {
        elapsed.Text = startedAt is { } started ? FormatElapsed(DateTimeOffset.Now - started) : "Codex closed";
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
