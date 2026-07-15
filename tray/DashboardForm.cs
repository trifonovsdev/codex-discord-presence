namespace CodexPresence;

public sealed class DashboardForm : Form
{
    private readonly Label connection = Visuals.Label("Connecting…", 9, true);
    private readonly Label project = Visuals.Label("—", 18, false, FontStyle.Bold);
    private readonly Label file = Visuals.Label("Waiting for Codex activity", 10, true);
    private readonly Label elapsed = Visuals.Label("00:00:00", 10, true);
    private readonly Button pause = Visuals.Button("Pause presence");

    public event EventHandler? PauseRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? DiagnosticsRequested;

    public DashboardForm()
    {
        Text = "Codex Presence";
        Icon = Visuals.CreateIcon();
        ClientSize = new Size(520, 330);
        MinimumSize = new Size(520, 330);
        MaximumSize = new Size(760, 420);
        BackColor = Visuals.Background;
        ForeColor = Visuals.Text;
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;

        var header = new Panel { Dock = DockStyle.Top, Height = 78, Padding = new Padding(22, 18, 22, 10), BackColor = Visuals.Surface };
        var title = Visuals.Label("CODEX PRESENCE", 13, false, FontStyle.Bold);
        title.Location = new Point(22, 17);
        connection.Location = new Point(23, 46);
        header.Controls.AddRange([title, connection]);

        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22), BackColor = Visuals.Background };
        var projectCaption = Visuals.Label("ACTIVE PROJECT", 8, true, FontStyle.Bold);
        projectCaption.Location = new Point(22, 21);
        project.Location = new Point(20, 43);
        project.MaximumSize = new Size(460, 34);
        file.Location = new Point(22, 82);
        file.MaximumSize = new Size(460, 42);
        elapsed.Location = new Point(22, 112);

        pause.SetBounds(22, 168, 142, 40);
        pause.Click += (_, _) => PauseRequested?.Invoke(this, EventArgs.Empty);
        var settings = Visuals.Button("Settings");
        settings.SetBounds(174, 168, 122, 40);
        settings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        var doctor = Visuals.Button("Run doctor");
        doctor.SetBounds(306, 168, 122, 40);
        doctor.Click += (_, _) => DiagnosticsRequested?.Invoke(this, EventArgs.Empty);
        content.Controls.AddRange([projectCaption, project, file, elapsed, pause, settings, doctor]);
        Controls.Add(content);
        Controls.Add(header);

        FormClosing += (_, eventArgs) =>
        {
            if (eventArgs.CloseReason == CloseReason.UserClosing)
            {
                eventArgs.Cancel = true;
                Hide();
            }
        };
    }

    public void UpdateSnapshot(HealthSnapshot? health)
    {
        if (health is null)
        {
            connection.Text = "● Daemon unavailable";
            connection.ForeColor = Visuals.Danger;
            project.Text = "Not connected";
            file.Text = "Open diagnostics to inspect the installation";
            elapsed.Text = "—";
            pause.Enabled = false;
            return;
        }

        connection.Text = health.RpcReady ? "● Discord connected" : "● Waiting for Discord";
        connection.ForeColor = health.RpcReady ? Visuals.Success : Visuals.Muted;
        project.Text = string.IsNullOrWhiteSpace(health.Project) ? "Waiting for project" : health.Project;
        file.Text = string.IsNullOrWhiteSpace(health.File) ? "No edited file yet" : health.File;
        elapsed.Text = health.CodexStartedAt is { } started
            ? $"Session  {FormatElapsed(DateTimeOffset.Now - started)}{(health.SelectedRemote is { Length: > 0 } remote ? $"  ·  {remote}" : "")}" : "Codex is not running";
        pause.Text = health.PresenceEnabled ? "Pause presence" : "Resume presence";
        pause.Enabled = true;
    }

    private static string FormatElapsed(TimeSpan value) => value.TotalHours >= 24
        ? $"{(int)value.TotalDays}d {value:hh\\:mm\\:ss}"
        : value.ToString("hh\\:mm\\:ss");
}
