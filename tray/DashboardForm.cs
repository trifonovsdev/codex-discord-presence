namespace CodexPresence;

public sealed class DashboardForm : ModernForm
{
    private readonly StatusPill connection = new() { Text = "Connecting" };
    private readonly Label project = Visuals.Label("Waiting for Codex", 19, false, FontStyle.Bold);
    private readonly Label file = Visuals.Label("No activity yet", 10, true);
    private readonly Label elapsed = Visuals.Label("00:00:00", 10, false, FontStyle.Bold);
    private readonly Label source = Visuals.Label("Local", 9, false, FontStyle.Bold);
    private readonly Label workspace = Visuals.Label("Desktop", 9, false, FontStyle.Bold);
    private readonly ModernButton pause = Visuals.Button("Pause", ButtonKind.Primary, "Ⅱ");

    public event EventHandler? PauseRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? DiagnosticsRequested;

    public DashboardForm() : base("Codex Presence", new Size(620, 420))
    {
        MaximumSize = new Size(620, 420);

        var heading = Visuals.Label("Presence", 17, false, FontStyle.Bold);
        heading.Location = new Point(28, 21);
        var subtitle = Visuals.Label("Current Codex activity shared with Discord", 9, true);
        subtitle.Location = new Point(29, 49);
        connection.Location = new Point(448, 24);

        var activity = new RoundedPanel
        {
            Location = new Point(28, 78),
            Size = new Size(562, 214),
            Radius = 14,
            BackColor = Visuals.Surface,
            BorderColor = Visuals.Border,
        };

        var eyebrow = Visuals.Eyebrow("Now editing");
        eyebrow.Location = new Point(20, 18);
        project.Location = new Point(19, 45);
        project.MaximumSize = new Size(515, 32);
        file.Location = new Point(20, 79);
        file.MaximumSize = new Size(515, 34);

        var divider = new Panel { Location = new Point(20, 119), Size = new Size(522, 1), BackColor = Visuals.BorderSoft };
        var signalCaption = Visuals.Eyebrow("Signal"); signalCaption.Location = new Point(20, 139);
        source.Location = new Point(20, 163);
        var workspaceCaption = Visuals.Eyebrow("Workspace"); workspaceCaption.Location = new Point(196, 139);
        workspace.Location = new Point(196, 163);
        var timeCaption = Visuals.Eyebrow("Codex session"); timeCaption.Location = new Point(392, 139);
        elapsed.Location = new Point(392, 162);

        activity.Controls.AddRange([
            eyebrow, project, file, divider,
            signalCaption, source, workspaceCaption, workspace, timeCaption, elapsed,
        ]);

        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 76,
            BackColor = Visuals.Canvas,
        };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Visuals.BorderSoft);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };

        pause.SetBounds(28, 17, 164, 42);
        pause.Click += (_, _) => PauseRequested?.Invoke(this, EventArgs.Empty);
        var settings = Visuals.Button("Settings", ButtonKind.Secondary, "⚙");
        settings.SetBounds(204, 17, 148, 42);
        settings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        var doctor = Visuals.Button("Doctor", ButtonKind.Ghost, "+");
        doctor.SetBounds(362, 17, 124, 42);
        doctor.Click += (_, _) => DiagnosticsRequested?.Invoke(this, EventArgs.Empty);
        var version = Visuals.Label("2.1.0", 8, true);
        version.Location = new Point(548, 31);
        footer.Controls.AddRange([pause, settings, doctor, version]);

        ContentHost.Controls.AddRange([heading, subtitle, connection, activity, footer]);
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
            connection.Text = "Service offline";
            connection.DotColor = Visuals.Danger;
            connection.FillColor = Visuals.DangerSurface;
            project.Text = "Not connected";
            file.Text = "Run Doctor to inspect the local service";
            elapsed.Text = "—";
            source.Text = "Unavailable";
            workspace.Text = "Unknown";
            pause.Enabled = false;
            connection.Invalidate();
            return;
        }

        connection.Text = health.RpcReady ? "Live on Discord" : "Waiting for Discord";
        connection.DotColor = health.RpcReady ? Visuals.Success : Visuals.Muted;
        connection.FillColor = health.RpcReady ? Visuals.SuccessSurface : Visuals.SurfaceRaised;
        connection.Invalidate();
        project.Text = string.IsNullOrWhiteSpace(health.Project) ? "Waiting for project" : health.Project;
        file.Text = string.IsNullOrWhiteSpace(health.File) ? "No edited file yet" : health.File;
        elapsed.Text = health.CodexStartedAt is { } started ? FormatElapsed(DateTimeOffset.Now - started) : "Codex is closed";
        source.Text = FriendlySource(health.Source);
        workspace.Text = health.SelectedRemote is { Length: > 0 } remote ? remote : "Local desktop";
        pause.Text = health.PresenceEnabled ? "Pause presence" : "Resume presence";
        pause.IconGlyph = health.PresenceEnabled ? "Ⅱ" : "▶";
        pause.Enabled = true;
        pause.Invalidate();
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
}
