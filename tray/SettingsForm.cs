using System.ComponentModel;

namespace CodexPresence;

public sealed class SettingsForm : ModernForm
{
    private readonly ConfigStore store;
    private readonly RemoteService remoteService;
    private readonly PresenceConfig config;
    private readonly ToggleRow enabled = new("Discord presence", "Publish activity while Codex is open");
    private readonly ToggleRow startup = new("Launch at sign in", "Keep presence available after Windows starts");
    private readonly ToggleRow updates = new("Automatic updates", "Check verified GitHub releases once a day");
    private readonly ModernSelect preset = Visuals.Select(["minimal", "standard", "detailed"]);
    private readonly ToggleRow showProject = new("Project name", "Show the active workspace on your Discord card");
    private readonly ToggleRow showFile = new("Edited file", "Show the latest file touched in the selected task");
    private readonly ToggleRow showTimer = new("Session timer", "Use one stable timer for the whole Codex session");
    private readonly ModernSelect fileMode = Visuals.Select(["name", "relative"]);
    private readonly ModernSelect pollInterval = Visuals.Select(["3 seconds", "5 seconds", "7 seconds", "10 seconds", "15 seconds", "30 seconds"], 150);
    private readonly BindingList<RemoteRow> remoteRows = [];
    private readonly DataGridView remotes = new();
    private readonly Panel pageHost = new() { Dock = DockStyle.Fill, BackColor = Visuals.Background };
    private readonly List<ModernButton> navigation = [];

    public bool Saved { get; private set; }

    public SettingsForm(ConfigStore store, RemoteService remoteService) : base("Settings", new Size(920, 660))
    {
        this.store = store;
        this.remoteService = remoteService;
        config = store.Load();
        MaximumSize = new Size(1100, 760);

        var sidebar = new Panel { Dock = DockStyle.Left, Width = 214, BackColor = Visuals.Canvas, Padding = new Padding(14, 22, 14, 16) };
        var section = Visuals.Eyebrow("Preferences");
        section.Location = new Point(20, 18);
        sidebar.Controls.Add(section);
        AddNavigation(sidebar, "General", "•", 52, BuildGeneralPage);
        AddNavigation(sidebar, "Privacy", "○", 100, BuildPrivacyPage);
        AddNavigation(sidebar, "SSH workspaces", "↗", 148, BuildRemotePage);

        var localNote = new RoundedPanel { Location = new Point(14, 500), Size = new Size(186, 82), Anchor = AnchorStyles.Left | AnchorStyles.Bottom, Radius = 12, BackColor = Visuals.Surface };
        var shield = Visuals.Label("◇  Local-first", 9, false, FontStyle.Bold); shield.Location = new Point(14, 13);
        var note = Visuals.Label("No prompt content or tokens\nleave this device.", 8, true); note.Location = new Point(14, 39);
        localNote.Controls.AddRange([shield, note]);
        sidebar.Controls.Add(localNote);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 72, BackColor = Visuals.Canvas };
        var divider = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Visuals.BorderSoft };
        var save = Visuals.Button("Save changes", ButtonKind.Primary);
        save.SetBounds(0, 15, 142, 42);
        save.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        save.Click += SaveClicked;
        var cancel = Visuals.Button("Cancel", ButtonKind.Ghost);
        cancel.SetBounds(0, 15, 92, 42);
        cancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        cancel.Click += (_, _) => Close();
        footer.Controls.AddRange([divider, cancel, save]);
        footer.Resize += (_, _) => { save.Left = footer.Width - save.Width - 24; cancel.Left = save.Left - cancel.Width - 8; };

        ContentHost.Controls.Add(pageHost);
        ContentHost.Controls.Add(footer);
        ContentHost.Controls.Add(sidebar);
        LoadValues();
        ShowPage(navigation[0], BuildGeneralPage);
    }

    private void AddNavigation(Control parent, string text, string icon, int y, Func<Control> pageFactory)
    {
        var button = Visuals.Button(text, ButtonKind.Ghost, icon);
        button.SetBounds(14, y, 186, 40);
        button.TextAlign = ContentAlignment.MiddleLeft;
        button.Click += (_, _) => ShowPage(button, pageFactory);
        navigation.Add(button);
        parent.Controls.Add(button);
    }

    private void ShowPage(ModernButton selected, Func<Control> pageFactory)
    {
        foreach (var item in navigation) { item.Kind = item == selected ? ButtonKind.Secondary : ButtonKind.Ghost; item.Invalidate(); }
        pageHost.Controls.Clear();
        var page = pageFactory();
        page.Dock = DockStyle.Fill;
        pageHost.Controls.Add(page);
    }

    private Control BuildGeneralPage()
    {
        var page = Page("General", "Control how Codex Presence starts and stays up to date.");
        enabled.SetBounds(34, 102, 620, 72);
        startup.SetBounds(34, 186, 620, 72);
        updates.SetBounds(34, 270, 620, 72);
        AnchorRows(page, enabled, startup, updates);
        return page;
    }

    private Control BuildPrivacyPage()
    {
        var page = Page("Privacy", "Choose what friends can see. Every signal stays local.");
        var presetCard = FieldCard("Privacy preset", "A quick baseline for your Discord card", preset);
        presetCard.SetBounds(34, 102, 620, 82);
        preset.SelectedIndexChanged -= PresetChanged;
        preset.SelectedIndexChanged += PresetChanged;
        showProject.SetBounds(34, 196, 620, 72);
        showFile.SetBounds(34, 280, 620, 72);
        showTimer.SetBounds(34, 364, 620, 72);
        var fileCard = FieldCard("File display", "Filename only or repository-relative path", fileMode);
        fileCard.SetBounds(34, 448, 620, 82);
        AnchorRows(page, presetCard, showProject, showFile, showTimer, fileCard);
        return page;
    }

    private Control BuildRemotePage()
    {
        var page = Page("SSH workspaces", "Map remote roots to servers. The most specific root wins.");
        ConfigureGrid();
        remotes.SetBounds(34, 102, 620, 262);
        remotes.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        var add = Visuals.Button("Add workspace", ButtonKind.Secondary, "+");
        add.SetBounds(34, 378, 140, 40);
        add.Click += (_, _) => remoteRows.Add(new RemoteRow());
        var remove = Visuals.Button("Remove", ButtonKind.Ghost, "−");
        remove.SetBounds(182, 378, 108, 40);
        remove.Click += (_, _) => { if (remotes.CurrentRow?.DataBoundItem is RemoteRow row) remoteRows.Remove(row); };
        var test = Visuals.Button("Test SSH", ButtonKind.Secondary, "↗");
        test.SetBounds(302, 378, 116, 40);
        test.Click += async (_, _) => await RunRemoteAction(false);
        var install = Visuals.Button("Install helper", ButtonKind.Primary);
        install.SetBounds(430, 378, 132, 40);
        install.Click += async (_, _) => await RunRemoteAction(true);
        var pollCard = FieldCard("Refresh interval", "How often the selected remote task is checked", pollInterval);
        pollCard.SetBounds(34, 438, 620, 82);
        AnchorRows(page, remotes, add, remove, test, install, pollCard);
        return page;
    }

    private static Panel Page(string titleText, string subtitleText)
    {
        var page = new Panel { BackColor = Visuals.Background, Padding = new Padding(34) };
        var title = Visuals.Label(titleText, 20, false, FontStyle.Bold); title.Location = new Point(34, 26);
        var subtitle = Visuals.Label(subtitleText, 9, true); subtitle.Location = new Point(35, 61);
        page.Controls.AddRange([title, subtitle]);
        return page;
    }

    private static RoundedPanel FieldCard(string titleText, string descriptionText, Control input)
    {
        var card = new RoundedPanel { Radius = 12, BackColor = Visuals.Surface };
        var title = Visuals.Label(titleText, 10, false, FontStyle.Bold); title.Location = new Point(16, 15);
        var description = Visuals.Label(descriptionText, 8.5f, true); description.Location = new Point(16, 43);
        input.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        card.Controls.AddRange([title, description, input]);
        card.Resize += (_, _) => input.Location = new Point(card.Width - input.Width - 16, 22);
        return card;
    }

    private static void AnchorRows(Control page, params Control[] controls)
    {
        foreach (var control in controls)
        {
            if (control.Width >= 500) control.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(control);
        }
        page.Resize += (_, _) =>
        {
            foreach (var control in controls.Where(item => item.Anchor.HasFlag(AnchorStyles.Right))) control.Width = Math.Max(480, page.ClientSize.Width - 68);
        };
    }

    private void ConfigureGrid()
    {
        if (remotes.Columns.Count > 0) return;
        remotes.BackgroundColor = Visuals.Surface;
        remotes.GridColor = Visuals.BorderSoft;
        remotes.BorderStyle = BorderStyle.None;
        remotes.EnableHeadersVisualStyles = false;
        remotes.ColumnHeadersHeight = 40;
        remotes.RowTemplate.Height = 42;
        remotes.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Visuals.SurfaceRaised,
            ForeColor = Visuals.TextSecondary,
            SelectionBackColor = Visuals.SurfaceRaised,
            Font = Visuals.Font(8.5f, FontStyle.Bold),
            Padding = new Padding(8, 0, 0, 0),
            Alignment = DataGridViewContentAlignment.MiddleLeft,
        };
        remotes.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Visuals.Surface,
            ForeColor = Visuals.Text,
            SelectionBackColor = Visuals.SurfaceHover,
            SelectionForeColor = Visuals.Text,
            Font = Visuals.Font(9),
            Padding = new Padding(8, 0, 0, 0),
        };
        remotes.AutoGenerateColumns = false;
        remotes.AllowUserToAddRows = false;
        remotes.AllowUserToResizeRows = false;
        remotes.RowHeadersVisible = false;
        remotes.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        remotes.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(RemoteRow.Name), HeaderText = "NAME", Width = 135 });
        remotes.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(RemoteRow.Host), HeaderText = "USER@HOST", Width = 180 });
        remotes.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(RemoteRow.Roots), HeaderText = "WORKSPACE ROOTS", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        remotes.DataSource = remoteRows;
        remotes.EditingControlShowing += (_, args) => { args.Control.BackColor = Visuals.SurfaceRaised; args.Control.ForeColor = Visuals.Text; };
    }

    private async Task RunRemoteAction(bool install)
    {
        remotes.EndEdit();
        if (remotes.CurrentRow?.DataBoundItem is not RemoteRow row || string.IsNullOrWhiteSpace(row.Host)) return;
        UseWaitCursor = true;
        var result = install ? await remoteService.InstallHelperAsync(row.ToConfig()) : await remoteService.TestAsync(row.ToConfig());
        UseWaitCursor = false;
        ModernDialog.Show(this, result.Ok ? "Connection ready" : "SSH failed", result.Output, result.Ok);
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
        var seconds = Math.Clamp(config.Remote.PollIntervalMs / 1000, 3, 60);
        pollInterval.Text = pollInterval.Options.OrderBy(value => Math.Abs(ParseSeconds(value) - seconds)).First();
        foreach (var remote in config.Remote.Hosts) remoteRows.Add(RemoteRow.FromConfig(remote));
        if (!config.Remote.Hosts.Any() && !string.IsNullOrWhiteSpace(config.Remote.Host)) remoteRows.Add(new RemoteRow { Name = config.Remote.Host, Host = config.Remote.Host });
    }

    private void SaveClicked(object? sender, EventArgs eventArgs)
    {
        remotes.EndEdit();
        if (remoteRows.Any(row => !string.IsNullOrWhiteSpace(row.Host) && !System.Text.RegularExpressions.Regex.IsMatch(row.Host, "^[A-Za-z0-9._@:-]+$")))
        {
            ModernDialog.Show(this, "Invalid SSH host", "Use only letters, numbers, dots, colons, dashes, underscores, and @.", false);
            return;
        }
        config.PresenceEnabled = enabled.Checked;
        config.Updates.Enabled = updates.Checked;
        config.Privacy = new PrivacyConfig { Preset = preset.Text, ShowProject = showProject.Checked, ShowFile = showFile.Checked, ShowTimer = showTimer.Checked, FileMode = fileMode.Text };
        config.Remote.Host = "";
        config.Remote.Hosts = remoteRows.Where(row => !string.IsNullOrWhiteSpace(row.Host)).Select(row => row.ToConfig()).ToList();
        config.Remote.PollIntervalMs = ParseSeconds(pollInterval.Text) * 1000;
        store.Save(config);
        store.StartsWithWindows = startup.Checked;
        Saved = true;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void PresetChanged(object? sender, EventArgs eventArgs) => ApplyPreset(preset.Text);
    private void ApplyPreset(string value)
    {
        showProject.Checked = true;
        showFile.Checked = value != "minimal";
        showTimer.Checked = true;
        fileMode.Text = value == "minimal" ? "name" : "relative";
    }

    private static int ParseSeconds(string value) => int.TryParse(value.Split(' ')[0], out var seconds) ? seconds : 7;

    private sealed class RemoteRow
    {
        public string Name { get; set; } = "Remote";
        public string Host { get; set; } = "";
        public string Roots { get; set; } = "";
        public RemoteHostConfig ToConfig() => new() { Name = Name.Trim(), Host = Host.Trim(), Roots = Roots.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() };
        public static RemoteRow FromConfig(RemoteHostConfig value) => new() { Name = value.Name, Host = value.Host, Roots = string.Join("; ", value.Roots) };
    }
}

public sealed class ModernDialog : ModernForm
{
    private ModernDialog(string titleText, string body, bool success) : base(titleText, new Size(440, 240))
    {
        MaximumSize = new Size(440, 240);
        var badge = new StatusPill
        {
            Text = success ? "Ready" : "Needs attention",
            DotColor = success ? Visuals.Success : Visuals.Danger,
            FillColor = success ? Visuals.SuccessSurface : Visuals.DangerSurface,
            Location = new Point(24, 22),
        };
        var message = Visuals.Label(body, 9.5f, true);
        message.Location = new Point(25, 67);
        message.MaximumSize = new Size(388, 78);
        var close = Visuals.Button("Done", ButtonKind.Primary);
        close.SetBounds(292, 148, 120, 42);
        close.Click += (_, _) => Close();
        ContentHost.Controls.AddRange([badge, message, close]);
    }

    public static void Show(IWin32Window owner, string title, string body, bool success)
    {
        using var dialog = new ModernDialog(title, body, success);
        dialog.ShowDialog(owner);
    }
}
