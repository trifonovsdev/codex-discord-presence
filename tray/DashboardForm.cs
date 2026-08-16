namespace CodexPresence;

/// <summary>
/// Compact controller for the one job this app performs: publish the selected
/// Codex activity to Discord. Privacy details stay in Settings instead of being
/// duplicated as dashboard furniture.
/// </summary>
public sealed class DashboardForm : ModernForm
{
    private readonly StatusPill connection = new()
    {
        Text = "Connecting",
        FillColor = Color.Transparent,
        DotColor = Visuals.Muted,
    };
    private readonly AnimatedText project = new()
    {
        Text = "Waiting for Codex",
        Font = Visuals.DisplayFont(22, FontStyle.Bold),
        TextColor = Visuals.Text,
        Height = 40,
        TravelDp = 2,
    };
    private readonly AnimatedText file = new()
    {
        Text = "No activity yet",
        Font = Visuals.MonoFont(9.25f),
        TextColor = Visuals.TextSecondary,
        Height = 28,
        TravelDp = 2,
    };
    private readonly Label activityContext = Visuals.Label("Selected task · Local desktop", 8.5f, true);
    private readonly Label SharingSummary = Visuals.Label("Sharing: project · file · timer", 8.25f, true);
    private readonly ModernButton pause = Visuals.Button("Pause", ButtonKind.Primary, UiIcon.Pause);
    private readonly ModernButton copyPath = Visuals.Button(string.Empty, ButtonKind.Ghost, UiIcon.Copy);
    private readonly ModernButton settings = Visuals.Button("Settings", ButtonKind.Ghost, UiIcon.Settings);
    private readonly ModernButton doctor = Visuals.Button("Doctor", ButtonKind.Ghost, UiIcon.Diagnostics);
    private readonly RoundedPanel alert = new()
    {
        Radius = 5,
        BorderWidth = 0,
        BackColor = Visuals.DangerSurface,
        Height = 38,
        Visible = false,
    };
    private readonly IconView alertIcon = new(UiIcon.Warning) { IconColor = Visuals.Danger, Size = new Size(17, 17) };
    private readonly Label alertText = Visuals.Label("", 8.5f);
    private readonly ModernButton alertAction = Visuals.Button("Doctor", ButtonKind.Ghost, UiIcon.Diagnostics);
    private readonly DiscordCardPreview preview = new() { Height = 132, Published = false };
    private readonly System.Windows.Forms.Timer ticker = new() { Interval = 1000 };
    private readonly ToolTip tooltip = new();
    private readonly BufferedFlowLayoutPanel content = new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
        BackColor = Visuals.Background,
        Padding = new Padding(28, 18, 28, 18),
    };

    private DateTimeOffset? startedAt;
    private string? pathToCopy;
    private string? lastAlertMessage;
    private string? lastAnnouncedAlert;
    private string elapsedText = "00:00:00";

    public event EventHandler? PauseRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? DiagnosticsRequested;

    public DashboardForm(string version) : base("Codex Presence", new Size(720, 440), resizable: true)
    {
        MinimumSize = SizeFromClientSize(new Size(660, 410));
        AccessibleDescription = $"Codex Presence {version}";

        var toolbar = BuildToolbar();
        var fileLine = BuildFileLine();
        var divider = Divider();
        var previewHeader = BuildPreviewHeader();
        var footer = BuildFooter();
        ConfigureAlert();

        activityContext.AutoSize = false;
        activityContext.AutoEllipsis = true;
        activityContext.Height = 18;
        activityContext.Margin = Padding.Empty;
        project.Margin = new Padding(0, 0, 0, 1);
        fileLine.Margin = new Padding(0, 0, 0, 4);
        alert.Margin = new Padding(0, 4, 0, 8);
        divider.Margin = new Padding(0, 8, 0, 11);
        previewHeader.Margin = new Padding(0, 0, 0, 7);
        preview.Margin = new Padding(0, 0, 0, 13);
        footer.Margin = Padding.Empty;

        content.Controls.AddRange([toolbar, activityContext, project, fileLine, alert, divider, previewHeader, preview, footer]);
        content.Resize += (_, _) => LayoutContent();
        ContentHost.Controls.Add(content);
        LayoutContent();

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

    private Panel BuildToolbar()
    {
        var toolbar = new Panel { Height = 36, BackColor = Visuals.Background, Margin = new Padding(0, 0, 0, 14) };
        connection.Height = 28;
        connection.IsLive = false;
        connection.Location = new Point(0, 4);

        ConfigureCommand(doctor, "Run diagnostics", "Checks the service, Discord, Codex hooks, and SSH workspaces");
        ConfigureCommand(settings, "Open settings", "Changes privacy, startup, language, and SSH workspaces");
        doctor.Width = 102;
        settings.Width = 112;
        doctor.Click += (_, _) => DiagnosticsRequested?.Invoke(this, EventArgs.Empty);
        settings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        toolbar.Controls.AddRange([connection, doctor, settings]);
        toolbar.Resize += (_, _) =>
        {
            settings.Location = new Point(toolbar.Width - settings.Width, 0);
            doctor.Location = new Point(settings.Left - doctor.Width - toolbar.Dp(4), 0);
        };
        return toolbar;
    }

    private void ConfigureCommand(ModernButton command, string name, string description)
    {
        command.Height = 34;
        command.Radius = 5;
        command.AccessibleName = name;
        command.AccessibleDescription = description;
        tooltip.SetToolTip(command, name);
    }

    private Panel BuildFileLine()
    {
        var row = new Panel { Height = 32, BackColor = Visuals.Background };
        file.AccessibleDescription = "Current repository-relative file";
        copyPath.Size = new Size(36, 32);
        copyPath.Enabled = false;
        copyPath.AccessibleName = "Copy current path";
        copyPath.AccessibleDescription = "Copies the repository-relative file path";
        copyPath.Click += (_, _) => CopyCurrentPath();
        tooltip.SetToolTip(copyPath, "Copy repository-relative path");
        row.Controls.AddRange([file, copyPath]);
        row.Resize += (_, _) =>
        {
            copyPath.Location = new Point(row.Width - copyPath.Width, 0);
            file.SetBounds(0, 2, Math.Max(0, copyPath.Left - row.Dp(12)), row.Dp(28));
        };
        return row;
    }

    private void ConfigureAlert()
    {
        alertIcon.Location = new Point(12, 10);
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
        alertAction.Size = new Size(96, 32);
        alertAction.AccessibleName = "Open Doctor for this warning";
        alertAction.Click += (_, _) => DiagnosticsRequested?.Invoke(this, EventArgs.Empty);
        alert.Controls.AddRange([alertIcon, alertText, alertAction]);
        alert.Resize += (_, _) =>
        {
            alertAction.Location = new Point(alert.Width - alertAction.Width - alert.Dp(3), alert.Dp(3));
            alertText.SetBounds(alert.Dp(40), 0, Math.Max(alert.Dp(100), alertAction.Left - alert.Dp(48)), alert.Height);
        };
    }

    private Panel BuildPreviewHeader()
    {
        var row = new Panel { Height = 24, BackColor = Visuals.Background };
        var heading = Visuals.Label("Discord now", 9.25f, false, FontStyle.Bold);
        heading.AutoSize = false;
        heading.TextAlign = ContentAlignment.MiddleLeft;
        SharingSummary.AutoSize = false;
        SharingSummary.AutoEllipsis = true;
        SharingSummary.TextAlign = ContentAlignment.MiddleRight;
        row.Controls.AddRange([heading, SharingSummary]);
        row.Resize += (_, _) =>
        {
            heading.SetBounds(0, 0, row.Width / 2, row.Height);
            SharingSummary.SetBounds(row.Width / 2, 0, row.Width - row.Width / 2, row.Height);
        };
        return row;
    }

    private Panel BuildFooter()
    {
        var footer = new Panel { Height = 38, BackColor = Visuals.Background };
        pause.Size = new Size(124, 38);
        pause.Click += (_, _) => PauseRequested?.Invoke(this, EventArgs.Empty);
        footer.Controls.Add(pause);
        footer.Resize += (_, _) =>
        {
            pause.Location = new Point(footer.Width - pause.Width, 0);
        };
        return footer;
    }

    private static Panel Divider() => new() { Height = 1, BackColor = Visuals.BorderSoft };

    private void LayoutContent()
    {
        var scrollbar = content.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0;
        var width = Math.Max(0, content.ClientSize.Width - content.Padding.Horizontal - scrollbar);
        foreach (Control control in content.Controls)
            control.Width = Math.Max(0, width - control.Margin.Horizontal);
        content.HorizontalScroll.Enabled = false;
        content.HorizontalScroll.Maximum = 0;
    }

    public void UpdateSnapshot(HealthSnapshot? health)
    {
        if (health is null)
        {
            startedAt = null;
            pathToCopy = null;
            SetConnection("Service offline", Visuals.Danger, live: false);
            activityContext.Text = "Local service unavailable";
            project.Text = "Service not connected";
            file.Text = "No activity is being published";
            elapsedText = "--:--:--";
            pause.Enabled = false;
            copyPath.Enabled = false;
            preview.ProjectName = null;
            preview.TaskTitle = null;
            preview.FileName = null;
            preview.PublishError = null;
            preview.Connected = false;
            preview.Published = false;
            preview.HasTimestamp = false;
            preview.Elapsed = elapsedText;
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

        var hasProject = !string.IsNullOrWhiteSpace(health.Project);
        project.Text = hasProject ? health.Project! : health.CodexRunning ? "Working in Codex" : "Waiting for Codex";
        file.Text = hasProject
            ? string.IsNullOrWhiteSpace(health.File) ? "No edited file yet" : health.File
            : health.CodexRunning ? "No detectable workspace for this task" : "Open a task to start sharing activity";
        pathToCopy = string.IsNullOrWhiteSpace(health.File) ? null : health.File;
        copyPath.Enabled = pathToCopy is not null;
        startedAt = health.CodexStartedAt;
        pause.Text = health.PresenceEnabled ? "Pause" : "Resume";
        pause.Icon = health.PresenceEnabled ? UiIcon.Pause : UiIcon.Play;
        pause.Enabled = true;

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
    }

    public void UpdatePrivacy(PrivacyConfig privacy)
    {
        preview.ShowProject = privacy.ShowProject;
        preview.ShowTaskTitle = privacy.ShowTaskTitle;
        preview.ShowFile = privacy.ShowFile;
        preview.ShowTimer = privacy.ShowTimer;
        preview.FileMode = privacy.FileMode;

        var shared = new List<string>();
        if (privacy.ShowProject) shared.Add("project");
        if (privacy.ShowTaskTitle) shared.Add("task");
        if (privacy.ShowFile) shared.Add("file");
        if (privacy.ShowTimer) shared.Add("timer");
        SharingSummary.Text = shared.Count == 0 ? "Sharing: nothing" : $"Sharing: {string.Join(" · ", shared)}";
    }

    private void SetConnection(string text, Color dot, bool live)
    {
        connection.Text = text;
        connection.DotColor = dot;
        connection.FillColor = Color.Transparent;
        connection.IsLive = live;
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
            AccessibleDescription = visible ? alertText.Text : AccessibleDescription;
            content.PerformLayout();
            LayoutContent();
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
        elapsedText = startedAt is { } started ? FormatElapsed(DateTimeOffset.Now - started) : "Codex closed";
        preview.Elapsed = elapsedText;
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
