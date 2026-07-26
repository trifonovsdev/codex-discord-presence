namespace CodexPresence;

public sealed class DashboardForm : ModernForm
{
    private readonly StatusPill connection = new() { Text = "Connecting" };
    private readonly Label project = Visuals.Label("Waiting for Codex", 19, false, FontStyle.Bold);
    private readonly Label file = Visuals.Label("No activity yet", 10, true);
    private readonly Label elapsed = Visuals.Label("00:00:00", 10, false, FontStyle.Bold);
    private readonly Label source = Visuals.Label("Local", 9, false, FontStyle.Bold);
    private readonly Label workspace = Visuals.Label("Desktop", 9, false, FontStyle.Bold);
    private readonly ModernButton pause = Visuals.Button("Pause presence", ButtonKind.Primary, "Ⅱ");
    private readonly RoundedPanel alert = new() { Radius = 10, BackColor = Visuals.DangerSurface, BorderColor = Visuals.DangerSurface, Visible = false };
    private readonly Label alertText = Visuals.Label("", 8.5f);
    private readonly System.Windows.Forms.Timer ticker = new() { Interval = 1000 };
    private readonly ToolTip tooltip = new();

    private DateTimeOffset? startedAt;

    public event EventHandler? PauseRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? DiagnosticsRequested;

    public DashboardForm(string version) : base("Codex Presence", new Size(620, 470))
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = Visuals.Background };
        var heading = Visuals.Label("Presence", 17, false, FontStyle.Bold);
        heading.Location = new Point(28, 21);
        var subtitle = Visuals.Label("Current Codex activity shared with Discord", 9, true);
        subtitle.Location = new Point(29, 49);
        connection.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        header.Controls.AddRange([heading, subtitle, connection]);
        header.Resize += (_, _) => connection.Location = new Point(header.Width - connection.Width - 28, 24);

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Visuals.Background, Padding = new Padding(28, 0, 28, 12) };
        var activity = new RoundedPanel { Dock = DockStyle.Fill, Radius = 14, BackColor = Visuals.Surface, BorderColor = Visuals.Border };

        var eyebrow = Visuals.Eyebrow("Now editing");
        eyebrow.Location = new Point(20, 18);
        project.Location = new Point(19, 45);
        project.AutoEllipsis = true;
        file.Location = new Point(20, 79);
        file.AutoEllipsis = true;
        file.Cursor = Cursors.Hand;
        tooltip.SetToolTip(file, "Click to copy the path");
        file.Click += (_, _) => CopyToClipboard(file.Text);

        var divider = new Panel { Location = new Point(20, 119), Height = 1, BackColor = Visuals.BorderSoft };

        // A three column grid keeps the stats aligned at every DPI and width.
        var stats = new TableLayoutPanel
        {
            Location = new Point(20, 135),
            Height = 52,
            ColumnCount = 3,
            RowCount = 2,
            BackColor = Color.Transparent,
        };
        stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        stats.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        stats.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        stats.Controls.Add(Visuals.Eyebrow("Signal"), 0, 0);
        stats.Controls.Add(Visuals.Eyebrow("Workspace"), 1, 0);
        stats.Controls.Add(Visuals.Eyebrow("Codex session"), 2, 0);
        stats.Controls.Add(source, 0, 1);
        stats.Controls.Add(workspace, 1, 1);
        stats.Controls.Add(elapsed, 2, 1);

        alert.Height = 34;
        alertText.ForeColor = Visuals.Danger;
        alertText.Location = new Point(12, 9);
        alertText.AutoEllipsis = true;
        alert.Controls.Add(alertText);

        activity.Controls.AddRange([eyebrow, project, file, divider, stats, alert]);
        activity.Resize += (_, _) =>
        {
            var inner = activity.Width - 40;
            divider.Width = inner;
            stats.Width = inner;
            alert.Width = inner;
            // Pinned to the bottom of the card so it can never collide with the stats row.
            alert.Location = new Point(20, Math.Max(stats.Bottom + 12, activity.Height - alert.Height - 16));
            project.MaximumSize = new Size(inner, 0);
            file.MaximumSize = new Size(inner, 0);
            alertText.MaximumSize = new Size(inner - 24, 0);
        };
        body.Controls.Add(activity);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 76, BackColor = Visuals.Canvas };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Visuals.BorderSoft);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };

        pause.SetBounds(28, 17, 176, 42);
        pause.Click += (_, _) => PauseRequested?.Invoke(this, EventArgs.Empty);
        var settings = Visuals.Button("Settings", ButtonKind.Secondary, "⚙");
        settings.SetBounds(216, 17, 148, 42);
        settings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        var doctor = Visuals.Button("Doctor", ButtonKind.Ghost, "✚");
        doctor.SetBounds(374, 17, 124, 42);
        doctor.Click += (_, _) => DiagnosticsRequested?.Invoke(this, EventArgs.Empty);
        var versionLabel = Visuals.Label(version, 8, true);
        versionLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        footer.Controls.AddRange([pause, settings, doctor, versionLabel]);
        footer.Resize += (_, _) => versionLabel.Location = new Point(footer.Width - versionLabel.Width - 24, 31);

        ContentHost.Controls.Add(body);
        ContentHost.Controls.Add(footer);
        ContentHost.Controls.Add(header);

        // Keeps the session clock moving between two-second health polls.
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
            SetConnection("Service offline", Visuals.Danger, Visuals.DangerSurface);
            project.Text = "Not connected";
            file.Text = "Run Doctor to inspect the local service";
            elapsed.Text = "—";
            source.Text = "Unavailable";
            workspace.Text = "Unknown";
            pause.Enabled = false;
            ShowAlert(null);
            return;
        }

        if (!health.PresenceEnabled) SetConnection("Paused", Visuals.Muted, Visuals.SurfaceRaised);
        else if (health.RpcReady) SetConnection("Live on Discord", Visuals.Success, Visuals.SuccessSurface);
        else SetConnection("Waiting for Discord", Visuals.Muted, Visuals.SurfaceRaised);

        project.Text = string.IsNullOrWhiteSpace(health.Project) ? "Waiting for project" : health.Project;
        file.Text = string.IsNullOrWhiteSpace(health.File) ? "No edited file yet" : health.File;
        source.Text = FriendlySource(health.Source);
        workspace.Text = health.SelectedRemote is { Length: > 0 } remote ? remote : "Local desktop";
        startedAt = health.CodexStartedAt;
        RenderElapsed();
        pause.Text = health.PresenceEnabled ? "Pause presence" : "Resume presence";
        pause.IconGlyph = health.PresenceEnabled ? "Ⅱ" : "▶";
        pause.Enabled = true;
        pause.Invalidate();
        ShowAlert(health.LastRemoteError);
    }

    private void SetConnection(string text, Color dot, Color fill)
    {
        connection.Text = text;
        connection.DotColor = dot;
        connection.FillColor = fill;
        connection.Invalidate();
    }

    /// <summary>Surfaces SSH failures on the dashboard instead of hiding them behind Doctor.</summary>
    private void ShowAlert(string? message)
    {
        var visible = !string.IsNullOrWhiteSpace(message);
        alert.Visible = visible;
        alertText.Text = visible ? $"SSH workspace: {message}" : string.Empty;
    }

    private void RenderElapsed() =>
        elapsed.Text = startedAt is { } started ? FormatElapsed(DateTimeOffset.Now - started) : "Codex is closed";

    private static void CopyToClipboard(string value)
    {
        try { if (!string.IsNullOrWhiteSpace(value)) Clipboard.SetText(value); }
        catch { }
    }

    private static string FriendlySource(string? value) => value switch
    {
        "desktop-route+remote-session" => "Remote task",
        "desktop-route+session" => "Desktop task",
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
