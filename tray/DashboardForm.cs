namespace CodexPresence;

public sealed class DashboardForm : ModernForm
{
    private readonly StatusPill connection = new() { Text = "Connecting" };
    private readonly Label project = Visuals.Label("Waiting for Codex", 21, false, FontStyle.Bold);
    private readonly Label file = Visuals.Label("No activity yet", 10, true);
    private readonly Label elapsed = Visuals.Label("00:00:00", 11, false, FontStyle.Bold);
    private readonly Label source = Visuals.Label("Local", 9, false, FontStyle.Bold);
    private readonly Label workspace = Visuals.Label("Desktop", 9, false, FontStyle.Bold);
    private readonly ModernButton pause = Visuals.Button("Pause", ButtonKind.Primary, "Ⅱ");

    public event EventHandler? PauseRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? DiagnosticsRequested;

    public DashboardForm() : base("Codex Presence", new Size(620, 456))
    {
        MaximumSize = new Size(620, 456);

        var heading = Visuals.Label("Presence", 18, false, FontStyle.Bold);
        heading.Location = new Point(28, 23);
        var subtitle = Visuals.Label("Your current Codex workspace on Discord", 9, true);
        subtitle.Location = new Point(29, 53);
        connection.Location = new Point(448, 28);

        var activity = new RoundedPanel
        {
            Location = new Point(28, 88),
            Size = new Size(562, 166),
            Radius = 16,
            BackColor = Visuals.Surface,
        };
        var eyebrow = Visuals.Eyebrow("Current activity");
        eyebrow.Location = new Point(20, 18);
        project.Location = new Point(18, 44);
        project.MaximumSize = new Size(515, 34);
        file.Location = new Point(20, 82);
        file.MaximumSize = new Size(515, 38);
        var divider = new Panel { Location = new Point(20, 118), Size = new Size(522, 1), BackColor = Visuals.BorderSoft };
        var clockIcon = Visuals.Label("◷", 11, true);
        clockIcon.Location = new Point(20, 132);
        elapsed.Location = new Point(44, 132);
        activity.Controls.AddRange([eyebrow, project, file, divider, clockIcon, elapsed]);

        var sourceCard = MetricCard("Signal", source, "Selected task");
        sourceCard.Location = new Point(28, 270);
        var workspaceCard = MetricCard("Workspace", workspace, "Local or SSH");
        workspaceCard.Location = new Point(310, 270);

        pause.SetBounds(28, 358, 164, 44);
        pause.Click += (_, _) => PauseRequested?.Invoke(this, EventArgs.Empty);
        var settings = Visuals.Button("Settings", ButtonKind.Secondary, "⚙");
        settings.SetBounds(204, 358, 154, 44);
        settings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        var doctor = Visuals.Button("Doctor", ButtonKind.Ghost, "＋");
        doctor.SetBounds(370, 358, 132, 44);
        doctor.Click += (_, _) => DiagnosticsRequested?.Invoke(this, EventArgs.Empty);
        var version = Visuals.Label("v2.1.0", 8, true);
        version.Location = new Point(542, 374);

        ContentHost.Controls.AddRange([heading, subtitle, connection, activity, sourceCard, workspaceCard, pause, settings, doctor, version]);
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

    private static RoundedPanel MetricCard(string caption, Label value, string hint)
    {
        var panel = new RoundedPanel { Size = new Size(280, 70), Radius = 12, BackColor = Visuals.Surface };
        var heading = Visuals.Eyebrow(caption); heading.Location = new Point(15, 12);
        value.Location = new Point(15, 34);
        var helper = Visuals.Label(hint, 8, true); helper.Location = new Point(154, 36);
        panel.Controls.AddRange([heading, value, helper]);
        return panel;
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
