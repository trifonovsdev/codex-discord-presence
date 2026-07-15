using System.ComponentModel;

namespace CodexPresence;

public sealed class SettingsForm : Form
{
    private readonly ConfigStore store;
    private readonly RemoteService remoteService;
    private readonly PresenceConfig config;
    private readonly CheckBox enabled = Check("Enable Discord presence");
    private readonly CheckBox startup = Check("Start with Windows");
    private readonly CheckBox updates = Check("Automatically check for updates");
    private readonly ComboBox preset = Combo(["minimal", "standard", "detailed"]);
    private readonly CheckBox showProject = Check("Show project name");
    private readonly CheckBox showFile = Check("Show edited file");
    private readonly CheckBox showTimer = Check("Show whole-app elapsed time");
    private readonly ComboBox fileMode = Combo(["name", "relative"]);
    private readonly NumericUpDown pollInterval = new() { Minimum = 3, Maximum = 60, Width = 90, BackColor = Visuals.SurfaceRaised, ForeColor = Visuals.Text, BorderStyle = BorderStyle.FixedSingle };
    private readonly BindingList<RemoteRow> remoteRows = [];
    private readonly DataGridView remotes = new();

    public bool Saved { get; private set; }

    public SettingsForm(ConfigStore store, RemoteService remoteService)
    {
        this.store = store;
        this.remoteService = remoteService;
        config = store.Load();
        Text = "Codex Presence Settings";
        Icon = Visuals.CreateIcon();
        ClientSize = new Size(760, 590);
        MinimumSize = new Size(760, 590);
        BackColor = Visuals.Background;
        ForeColor = Visuals.Text;
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterScreen;

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(18, 8), Appearance = TabAppearance.Normal };
        tabs.TabPages.Add(BuildGeneralPage());
        tabs.TabPages.Add(BuildPrivacyPage());
        tabs.TabPages.Add(BuildRemotePage());

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 68, Padding = new Padding(18, 14, 18, 14), BackColor = Visuals.Surface };
        var save = Visuals.Button("Save and restart", true);
        save.SetBounds(0, 14, 160, 40);
        save.Click += SaveClicked;
        var cancel = Visuals.Button("Cancel");
        cancel.SetBounds(0, 14, 102, 40);
        cancel.Click += (_, _) => Close();
        footer.Controls.AddRange([cancel, save]);
        footer.Resize += (_, _) =>
        {
            save.Left = footer.ClientSize.Width - save.Width - 18;
            cancel.Left = save.Left - cancel.Width - 10;
        };
        Controls.Add(tabs);
        Controls.Add(footer);
        footer.BringToFront();
        LoadValues();
    }

    private TabPage BuildGeneralPage()
    {
        var page = Page("General");
        var title = Visuals.Label("General", 18, false, FontStyle.Bold); title.Location = new Point(24, 24);
        var description = Visuals.Label("Control startup, presence availability, and update behavior.", 9, true); description.Location = new Point(25, 58);
        enabled.Location = new Point(28, 112);
        startup.Location = new Point(28, 154);
        updates.Location = new Point(28, 196);
        page.Controls.AddRange([title, description, enabled, startup, updates]);
        return page;
    }

    private TabPage BuildPrivacyPage()
    {
        var page = Page("Privacy");
        var title = Visuals.Label("Discord card privacy", 18, false, FontStyle.Bold); title.Location = new Point(24, 24);
        var description = Visuals.Label("Choose a preset, then override individual fields if needed.", 9, true); description.Location = new Point(25, 58);
        page.Controls.AddRange([title, description]);
        AddField(page, "Preset", preset, 105);
        preset.SelectedIndexChanged += (_, _) => ApplyPreset(preset.Text);
        showProject.Location = new Point(28, 166);
        showFile.Location = new Point(28, 208);
        showTimer.Location = new Point(28, 250);
        page.Controls.AddRange([showProject, showFile, showTimer]);
        AddField(page, "File path", fileMode, 304);
        return page;
    }

    private TabPage BuildRemotePage()
    {
        var page = Page("Remote workspaces");
        var title = Visuals.Label("SSH workspaces", 18, false, FontStyle.Bold); title.Location = new Point(24, 20);
        var description = Visuals.Label("Map workspace roots to SSH hosts. The longest matching root wins.", 9, true); description.Location = new Point(25, 53);
        remotes.SetBounds(24, 91, 692, 290);
        remotes.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        remotes.BackgroundColor = Visuals.Surface;
        remotes.GridColor = Visuals.SurfaceRaised;
        remotes.BorderStyle = BorderStyle.None;
        remotes.EnableHeadersVisualStyles = false;
        remotes.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Visuals.SurfaceRaised, ForeColor = Visuals.Text, SelectionBackColor = Visuals.SurfaceRaised };
        remotes.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Visuals.Surface, ForeColor = Visuals.Text, SelectionBackColor = Visuals.Accent, SelectionForeColor = Visuals.Text };
        remotes.AutoGenerateColumns = false;
        remotes.AllowUserToAddRows = false;
        remotes.RowHeadersVisible = false;
        remotes.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(RemoteRow.Name), HeaderText = "Name", Width = 130 });
        remotes.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(RemoteRow.Host), HeaderText = "user@host", Width = 190 });
        remotes.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(RemoteRow.Roots), HeaderText = "Workspace roots (; separated)", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        remotes.DataSource = remoteRows;

        var add = Visuals.Button("Add"); add.SetBounds(24, 396, 88, 38); add.Click += (_, _) => remoteRows.Add(new RemoteRow());
        var remove = Visuals.Button("Remove"); remove.SetBounds(120, 396, 88, 38); remove.Click += (_, _) => { if (remotes.CurrentRow?.DataBoundItem is RemoteRow row) remoteRows.Remove(row); };
        var test = Visuals.Button("Test SSH"); test.SetBounds(218, 396, 110, 38); test.Click += async (_, _) => await RunRemoteAction(false);
        var install = Visuals.Button("Install helper", true); install.SetBounds(338, 396, 132, 38); install.Click += async (_, _) => await RunRemoteAction(true);
        var pollLabel = Visuals.Label("Polling interval (seconds)", 9, true); pollLabel.Location = new Point(24, 458);
        pollInterval.Location = new Point(190, 454);
        page.Controls.AddRange([title, description, remotes, add, remove, test, install, pollLabel, pollInterval]);
        return page;
    }

    private async Task RunRemoteAction(bool install)
    {
        remotes.EndEdit();
        if (remotes.CurrentRow?.DataBoundItem is not RemoteRow row || string.IsNullOrWhiteSpace(row.Host)) return;
        UseWaitCursor = true;
        var remote = row.ToConfig();
        var result = install ? await remoteService.InstallHelperAsync(remote) : await remoteService.TestAsync(remote);
        UseWaitCursor = false;
        MessageBox.Show(this, result.Output, result.Ok ? "Success" : "SSH failed", MessageBoxButtons.OK, result.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
    }

    private void LoadValues()
    {
        enabled.Checked = config.PresenceEnabled;
        startup.Checked = store.StartsWithWindows;
        updates.Checked = config.Updates.Enabled;
        preset.Text = config.Privacy.Preset;
        showProject.Checked = config.Privacy.ShowProject;
        showFile.Checked = config.Privacy.ShowFile;
        showTimer.Checked = config.Privacy.ShowTimer;
        fileMode.Text = config.Privacy.FileMode;
        pollInterval.Value = Math.Clamp(config.Remote.PollIntervalMs / 1000, 3, 60);
        foreach (var remote in config.Remote.Hosts) remoteRows.Add(RemoteRow.FromConfig(remote));
        if (!config.Remote.Hosts.Any() && !string.IsNullOrWhiteSpace(config.Remote.Host)) remoteRows.Add(new RemoteRow { Name = config.Remote.Host, Host = config.Remote.Host });
    }

    private void SaveClicked(object? sender, EventArgs eventArgs)
    {
        remotes.EndEdit();
        if (remoteRows.Any(row => !string.IsNullOrWhiteSpace(row.Host) && !System.Text.RegularExpressions.Regex.IsMatch(row.Host, "^[A-Za-z0-9._@:-]+$")))
        {
            MessageBox.Show(this, "An SSH host contains unsupported characters.", "Invalid settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        config.PresenceEnabled = enabled.Checked;
        config.Updates.Enabled = updates.Checked;
        config.Privacy = new PrivacyConfig { Preset = preset.Text, ShowProject = showProject.Checked, ShowFile = showFile.Checked, ShowTimer = showTimer.Checked, FileMode = fileMode.Text };
        config.Remote.Host = "";
        config.Remote.Hosts = remoteRows.Where(row => !string.IsNullOrWhiteSpace(row.Host)).Select(row => row.ToConfig()).ToList();
        config.Remote.PollIntervalMs = (int)pollInterval.Value * 1000;
        store.Save(config);
        store.StartsWithWindows = startup.Checked;
        Saved = true;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void ApplyPreset(string value)
    {
        if (value == "minimal") { showProject.Checked = true; showFile.Checked = false; showTimer.Checked = true; fileMode.Text = "name"; }
        else if (value == "detailed") { showProject.Checked = true; showFile.Checked = true; showTimer.Checked = true; fileMode.Text = "relative"; }
        else { showProject.Checked = true; showFile.Checked = true; showTimer.Checked = true; fileMode.Text = "relative"; }
    }

    private static TabPage Page(string text) => new(text) { BackColor = Visuals.Background, ForeColor = Visuals.Text, Padding = new Padding(12) };
    private static CheckBox Check(string text) => new() { Text = text, ForeColor = Visuals.Text, AutoSize = true, Font = new Font("Segoe UI", 10f), FlatStyle = FlatStyle.Flat };
    private static ComboBox Combo(string[] values) { var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180, BackColor = Visuals.SurfaceRaised, ForeColor = Visuals.Text, FlatStyle = FlatStyle.Flat }; box.Items.AddRange(values); return box; }
    private static void AddField(Control parent, string label, Control input, int y) { var caption = Visuals.Label(label, 9, true); caption.Location = new Point(28, y + 5); input.Location = new Point(170, y); parent.Controls.AddRange([caption, input]); }

    private sealed class RemoteRow
    {
        public string Name { get; set; } = "Remote";
        public string Host { get; set; } = "";
        public string Roots { get; set; } = "";
        public RemoteHostConfig ToConfig() => new() { Name = Name.Trim(), Host = Host.Trim(), Roots = Roots.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() };
        public static RemoteRow FromConfig(RemoteHostConfig value) => new() { Name = value.Name, Host = value.Host, Roots = string.Join("; ", value.Roots) };
    }
}
