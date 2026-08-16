using System.ComponentModel;
using System.Text.RegularExpressions;

namespace CodexPresence;

public sealed partial class SettingsForm : ModernForm
{
    [GeneratedRegex("^[A-Za-z0-9._@:-]+$")] private static partial Regex HostPattern();
    [GeneratedRegex(@"^[A-Za-z0-9_./~-]+$")] private static partial Regex RootPattern();

    private static readonly (string Label, string Value)[] Languages = [("English", "en"), ("Русский", "ru")];

    private readonly ConfigStore store;
    private readonly RemoteService remoteService;
    private readonly PresenceConfig config;
    private readonly ToggleRow enabled = new("Discord presence", "Publish activity while Codex is open");
    private readonly ToggleRow startup = new("Launch at sign in", "Keep presence available after Windows starts");
    private readonly ToggleRow updates = new("Automatic updates", "Check verified GitHub releases once a day");
    private readonly ModernSelect language = Visuals.Select(Languages.Select(item => item.Label), 170);
    private readonly ModernSelect preset = Visuals.Select(["minimal", "standard", "detailed"], 126);
    private readonly ToggleRow showTaskTitle = new("Task title", "Show the selected Codex task title when it is available");
    private readonly ToggleRow showProject = new("Project name", "Show the active workspace on your Discord card");
    private readonly ToggleRow showFile = new("Edited file", "Show the latest file touched in the selected task");
    private readonly ToggleRow showTimer = new("Session timer", "Use one stable timer for the whole Codex session");
    private readonly ModernSelect fileMode = Visuals.Select(["name", "relative"], 126);
    private readonly ModernSelect pollInterval = Visuals.Select(["3 seconds", "5 seconds", "7 seconds", "10 seconds", "15 seconds", "30 seconds"], 170);
    private readonly BindingList<RemoteRow> remoteRows = [];
    private readonly DataGridView remotes = new();
    private readonly Label remoteEmptyState = Visuals.Label("No SSH workspaces yet. Add one to follow remote Codex tasks.", 9, true);
    private readonly Panel pageHost = new() { Dock = DockStyle.Fill, BackColor = Visuals.Background };
    private readonly Panel navigationIndicator = new() { Size = new Size(3, 24), BackColor = Visuals.Success };
    private readonly DiscordCardPreview privacyPreview = new() { Height = 188 };
    private readonly List<ModernButton> navigation = [];
    private readonly List<ModernButton> remoteActions = [];
    private readonly Dictionary<ModernButton, Control> pages = [];
    private IDisposable? navigationMotion;
    private CancellationTokenSource? remoteActionCancellation;

    public bool Saved { get; private set; }

    public SettingsForm(ConfigStore store, RemoteService remoteService) : base("Settings", new Size(920, 660), resizable: true)
    {
        this.store = store;
        this.remoteService = remoteService;
        config = store.Load();
        MinimumSize = new Size(820, 580);
        CloseOnEscape = true;

        var sidebar = new Panel { Dock = DockStyle.Left, Width = 214, BackColor = Visuals.Canvas };
        var section = Visuals.Eyebrow("Preferences");
        section.Location = new Point(20, 18);
        sidebar.Controls.Add(section);
        AddNavigation(sidebar, "General", UiIcon.General, 52, BuildGeneralPage);
        AddNavigation(sidebar, "Privacy", UiIcon.Privacy, 100, BuildPrivacyPage);
        AddNavigation(sidebar, "SSH workspaces", UiIcon.Remote, 148, BuildRemotePage);
        navigationIndicator.Location = new Point(14, 60);
        sidebar.Controls.Add(navigationIndicator);
        navigationIndicator.BringToFront();

        var localNote = new RoundedPanel { Size = new Size(186, 98), Anchor = AnchorStyles.Left | AnchorStyles.Bottom, Radius = 12, BackColor = Visuals.Surface };
        var shieldIcon = new IconView(UiIcon.Privacy) { Location = new Point(14, 13), Size = new Size(18, 18), IconColor = Visuals.Success };
        var shield = Visuals.Label("Local-first", 9, false, FontStyle.Bold);
        shield.Location = new Point(40, 14);
        var note = Visuals.Label("Task titles stay private unless\nyou enable sharing. Tokens never\nleave this device.", 8, true);
        note.Location = new Point(14, 39);
        localNote.Controls.AddRange([shieldIcon, shield, note]);
        sidebar.Controls.Add(localNote);
        sidebar.Resize += (_, _) => localNote.Location = new Point(14, sidebar.Height - localNote.Height - 20);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 72, BackColor = Visuals.Canvas };
        var divider = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Visuals.BorderSoft };
        var save = Visuals.Button("Save changes", ButtonKind.Primary);
        save.SetBounds(0, 15, 148, 42);
        save.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        save.Click += SaveClicked;
        var cancel = Visuals.Button("Cancel", ButtonKind.Ghost);
        cancel.SetBounds(0, 15, 96, 42);
        cancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        cancel.Click += (_, _) => Close();
        footer.Controls.AddRange([divider, cancel, save]);
        footer.Resize += (_, _) => { save.Left = footer.Width - save.Width - 24; cancel.Left = save.Left - cancel.Width - 8; };

        ContentHost.Controls.Add(pageHost);
        ContentHost.Controls.Add(footer);
        ContentHost.Controls.Add(sidebar);
        preset.SelectedIndexChanged += PresetChanged;
        foreach (var row in new[] { showTaskTitle, showProject, showFile, showTimer }) row.CheckedChanged += (_, _) => UpdatePrivacyPreview();
        fileMode.SelectedIndexChanged += (_, _) => UpdatePrivacyPreview();
        language.SelectedIndexChanged += (_, _) => UpdatePrivacyPreview();
        privacyPreview.ProjectName = "codex-discord-presence";
        privacyPreview.TaskTitle = "Polish the desktop presence";
        privacyPreview.FileName = "tray/DashboardForm.cs";
        privacyPreview.Connected = true;
        privacyPreview.Elapsed = "01:42:16";
        remoteRows.ListChanged += (_, _) => remoteEmptyState.Visible = remoteRows.Count == 0;
        FormClosing += (_, _) => remoteActionCancellation?.Cancel();
        LoadValues();
        ShowPage(navigation[0], BuildGeneralPage);
    }

    private void AddNavigation(Control parent, string text, UiIcon icon, int y, Func<Control> pageFactory)
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
        foreach (var item in navigation) item.Kind = item == selected ? ButtonKind.Secondary : ButtonKind.Ghost;
        navigationMotion?.Dispose();
        var start = navigationIndicator.Top;
        var target = selected.Top + (selected.Height - navigationIndicator.Height) / 2;
        navigationMotion = MotionClock.Animate(navigationIndicator, 180, value =>
        {
            navigationIndicator.Top = (int)Math.Round(MotionClock.Lerp(start, target, value));
            navigationIndicator.Invalidate();
        }, MotionEasing.EaseInOutCubic);
        foreach (Control existing in pageHost.Controls) existing.Visible = false;
        if (!pages.TryGetValue(selected, out var page))
        {
            page = pageFactory();
            page.Dock = DockStyle.Fill;
            pages[selected] = page;
            pageHost.Controls.Add(page);
        }
        page.Visible = true;
        page.BringToFront();
    }

    private Control BuildGeneralPage()
    {
        var page = Page("General", "Control how Codex Presence starts and stays up to date.");
        Stack(page, enabled, startup, updates, FieldCard("Card language", "Language of the text published to Discord", language));
        return page;
    }

    private Control BuildPrivacyPage()
    {
        var page = Page("Privacy", "Choose the project, task, file, and timer signals shared with Discord.");
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Visuals.Background,
            Padding = new Padding(34, 0, 24, 24),
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));

        var settingsColumn = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Visuals.Background,
            Margin = Padding.Empty,
        };
        foreach (var card in new Control[]
        {
            FieldCard("Privacy preset", "A quick baseline for your Discord card", preset),
            PrivacyToggleGroup(),
            FieldCard("File display", "Filename only or repository-relative path", fileMode),
        })
        {
            card.Margin = new Padding(0, 0, 0, 12);
            settingsColumn.Controls.Add(card);
        }
        settingsColumn.Resize += (_, _) =>
        {
            var width = Math.Max(260, settingsColumn.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
            foreach (Control card in settingsColumn.Controls) card.Width = width;
        };

        var previewColumn = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Visuals.Background,
            Padding = new Padding(12, 0, 0, 0),
            Margin = Padding.Empty,
        };
        privacyPreview.Margin = Padding.Empty;
        var previewNote = Visuals.Label("The preview stays visible while you change what Discord can see.", 8.25f, true);
        previewNote.Margin = new Padding(2, 12, 2, 0);
        previewColumn.Controls.AddRange([privacyPreview, previewNote]);
        previewColumn.Resize += (_, _) =>
        {
            var width = Math.Max(200, previewColumn.ClientSize.Width - previewColumn.Padding.Horizontal);
            privacyPreview.Width = width;
            previewNote.MaximumSize = new Size(width - 4, 0);
        };

        content.Controls.Add(settingsColumn, 0, 0);
        content.Controls.Add(previewColumn, 1, 0);
        page.Controls.Add(content);
        page.Controls.Cast<Control>().FirstOrDefault(control => control.Dock == DockStyle.Top)?.BringToFront();
        return page;
    }

    private RoundedPanel PrivacyToggleGroup()
    {
        var group = new RoundedPanel { Radius = 12, BackColor = Visuals.Surface, Height = 275 };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 7,
            ColumnCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
        };
        // Interleave the four rows with hairline separators.
        foreach (var height in new[] { 68f, 1f, 68f, 1f, 68f, 1f, 68f }) table.RowStyles.Add(new RowStyle(SizeType.Absolute, height));

        var toggles = new[] { showTaskTitle, showProject, showFile, showTimer };
        for (var index = 0; index < toggles.Length; index++)
        {
            var row = toggles[index];
            row.Dock = DockStyle.Fill;
            row.Margin = Padding.Empty;
            row.Radius = 0;
            row.BorderWidth = 0;
            table.Controls.Add(row, 0, index * 2);
            if (index == toggles.Length - 1) continue;
            table.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = Visuals.BorderSoft, Margin = new Padding(16, 0, 16, 0) }, 0, index * 2 + 1);
        }
        group.Controls.Add(table);
        return group;
    }

    private Control BuildRemotePage()
    {
        var page = Page("SSH workspaces", "Map remote roots to servers. The most specific root wins.");
        ConfigureGrid();

        var grid = new Panel { Height = 262, BackColor = Visuals.Background };
        remotes.Dock = DockStyle.Fill;
        grid.Controls.Add(remotes);
        remoteEmptyState.BackColor = Visuals.Surface;
        remoteEmptyState.Visible = remoteRows.Count == 0;
        grid.Controls.Add(remoteEmptyState);
        remoteEmptyState.BringToFront();
        grid.Resize += (_, _) => remoteEmptyState.Location = new Point(18, 62);

        var actions = new Panel { Height = 56, BackColor = Visuals.Background };
        remoteActions.Clear();
        AddRemoteAction(actions, "Add workspace", ButtonKind.Secondary, UiIcon.Add, 0, 148, (_, _) => remoteRows.Add(new RemoteRow()));
        AddRemoteAction(actions, "Remove", ButtonKind.Ghost, UiIcon.Remove, 156, 108, (_, _) =>
        {
            if (remotes.CurrentRow?.DataBoundItem is RemoteRow row) remoteRows.Remove(row);
        });
        AddRemoteAction(actions, "Test SSH", ButtonKind.Secondary, UiIcon.Remote, 272, 120, async (_, _) => await RunRemoteAction(false));
        AddRemoteAction(actions, "Install helper", ButtonKind.Primary, UiIcon.Check, 400, 140, async (_, _) => await RunRemoteAction(true));

        Stack(page, grid, actions, FieldCard("Refresh interval", "How often the selected remote task is checked", pollInterval));
        return page;
    }

    private void AddRemoteAction(Control parent, string text, ButtonKind kind, UiIcon? icon, int x, int width, EventHandler onClick)
    {
        var button = Visuals.Button(text, kind, icon);
        button.SetBounds(x, 8, width, 40);
        button.Click += onClick;
        remoteActions.Add(button);
        parent.Controls.Add(button);
    }

    private static Panel Page(string titleText, string subtitleText)
    {
        var page = new Panel { BackColor = Visuals.Background };
        var header = new Panel { Dock = DockStyle.Top, Height = 96, BackColor = Visuals.Background };
        var title = Visuals.Heading(titleText, 20);
        title.Location = new Point(34, 26);
        var subtitle = Visuals.Label(subtitleText, 9, true);
        subtitle.Location = new Point(35, 61);
        header.Controls.AddRange([title, subtitle]);
        page.Controls.Add(header);
        return page;
    }

    /// <summary>
    /// Lays cards out in a scrollable column. The previous build positioned
    /// every row with hard-coded pixel bounds, which broke as soon as the
    /// window or the system scaling changed.
    /// </summary>
    private static void Stack(Control page, params Control[] cards)
    {
        var column = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Visuals.Background,
            Padding = new Padding(34, 0, 24, 24),
        };
        foreach (var card in cards)
        {
            card.Margin = new Padding(0, 0, 0, 12);
            column.Controls.Add(card);
        }
        column.Resize += (_, _) =>
        {
            var width = Math.Max(420, column.ClientSize.Width - column.Padding.Horizontal);
            foreach (Control card in column.Controls) card.Width = width;
        };
        page.Controls.Add(column);
        page.Controls.Cast<Control>().FirstOrDefault(control => control.Dock == DockStyle.Top)?.BringToFront();
    }

    private static RoundedPanel FieldCard(string titleText, string descriptionText, Control input)
    {
        var card = new RoundedPanel { Radius = 12, BackColor = Visuals.Surface, Height = 82 };
        var title = Visuals.Label(titleText, 10, false, FontStyle.Bold);
        title.Location = new Point(16, 15);
        var description = Visuals.Label(descriptionText, 8.5f, true);
        description.Location = new Point(16, 43);
        input.AccessibleName = titleText;
        input.AccessibleDescription = descriptionText;
        input.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        card.Controls.AddRange([title, description, input]);
        card.Resize += (_, _) =>
        {
            input.Location = new Point(card.Width - input.Width - 16, (card.Height - input.Height) / 2);
            var textWidth = Math.Max(100, input.Left - 32);
            title.MaximumSize = new Size(textWidth, 0);
            description.MaximumSize = new Size(textWidth, 0);
        };
        return card;
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
        remotes.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        remotes.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
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
        remotes.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(RemoteRow.Name), HeaderText = "Name", Width = 135 });
        remotes.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(RemoteRow.Host), HeaderText = "User@host", Width = 180 });
        remotes.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(RemoteRow.Roots), HeaderText = "Workspace roots (; separated)", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        remotes.DataSource = remoteRows;
        remotes.EditingControlShowing += (_, args) => { args.Control.BackColor = Visuals.SurfaceRaised; args.Control.ForeColor = Visuals.Text; };
    }

    private async Task RunRemoteAction(bool install)
    {
        remotes.EndEdit();
        if (remotes.CurrentRow?.DataBoundItem is not RemoteRow row || string.IsNullOrWhiteSpace(row.Host))
        {
            ModernDialog.Show(this, "Select a workspace", "Add a row and fill in user@host before testing the connection.", false);
            return;
        }
        if (Validate(row) is { } problem)
        {
            ModernDialog.Show(this, "Check the workspace", problem, false);
            return;
        }

        remoteActionCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        remoteActionCancellation = cancellation;
        SetRemoteActionsEnabled(false);
        UseWaitCursor = true;
        try
        {
            var result = install
                ? await remoteService.InstallHelperAsync(row.ToConfig(), cancellation.Token)
                : await remoteService.TestAsync(row.ToConfig(), cancellation.Token);
            if (cancellation.IsCancellationRequested || IsDisposed || Disposing) return;
            ModernDialog.Show(this, result.Ok ? "Connection ready" : "SSH failed", result.Output, result.Ok);
        }
        finally
        {
            if (ReferenceEquals(remoteActionCancellation, cancellation)) remoteActionCancellation = null;
            cancellation.Dispose();
            if (!IsDisposed && !Disposing)
            {
                UseWaitCursor = false;
                SetRemoteActionsEnabled(true);
            }
        }
    }

    private void SetRemoteActionsEnabled(bool value)
    {
        foreach (var button in remoteActions) button.Enabled = value;
    }

    private void LoadValues()
    {
        enabled.Checked = config.PresenceEnabled;
        startup.Checked = store.StartsWithWindows;
        updates.Checked = config.Updates.Enabled;
        language.Text = Languages.FirstOrDefault(item => item.Value == config.Language).Label ?? Languages[0].Label;
        preset.Text = config.Privacy.Preset;
        showTaskTitle.Checked = config.Privacy.ShowTaskTitle;
        showProject.Checked = config.Privacy.ShowProject;
        showFile.Checked = config.Privacy.ShowFile;
        showTimer.Checked = config.Privacy.ShowTimer;
        fileMode.Text = config.Privacy.FileMode;
        var seconds = Math.Clamp(config.Remote.PollIntervalMs / 1000, 3, 60);
        pollInterval.Text = pollInterval.Options.OrderBy(value => Math.Abs(ParseSeconds(value) - seconds)).First();
        foreach (var remote in config.Remote.Hosts) remoteRows.Add(RemoteRow.FromConfig(remote));
        if (!config.Remote.Hosts.Any() && !string.IsNullOrWhiteSpace(config.Remote.Host)) remoteRows.Add(new RemoteRow { Name = config.Remote.Host, Host = config.Remote.Host });
        UpdatePrivacyPreview();
    }

    private static string? Validate(RemoteRow row)
    {
        if (!HostPattern().IsMatch(row.Host.Trim()))
            return $"“{row.Host}” is not a valid SSH host.\n\nUse only letters, numbers, dots, colons, dashes, underscores and @, for example dev@example.com.";
        var roots = row.Roots.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var badRoot = roots.FirstOrDefault(root => !RootPattern().IsMatch(root));
        if (badRoot is not null)
            return $"“{badRoot}” is not a valid workspace root.\n\nUse absolute POSIX paths such as /srv/store, separated by semicolons.";
        return null;
    }

    private void SaveClicked(object? sender, EventArgs eventArgs)
    {
        remotes.EndEdit();
        var filled = remoteRows.Where(row => !string.IsNullOrWhiteSpace(row.Host)).ToList();
        if (filled.Select(Validate).FirstOrDefault(problem => problem is not null) is { } invalid)
        {
            ModernDialog.Show(this, "Check the SSH workspaces", invalid, false);
            return;
        }

        config.PresenceEnabled = enabled.Checked;
        config.Updates.Enabled = updates.Checked;
        config.Language = Languages.FirstOrDefault(item => item.Label == language.Text).Value ?? "en";
        config.Privacy = new PrivacyConfig
        {
            Preset = preset.Text,
            ShowTaskTitle = showTaskTitle.Checked,
            ShowProject = showProject.Checked,
            ShowFile = showFile.Checked,
            ShowTimer = showTimer.Checked,
            FileMode = fileMode.Text,
        };
        config.Remote.Host = "";
        config.Remote.Hosts = filled.Select(row => row.ToConfig()).ToList();
        config.Remote.PollIntervalMs = ParseSeconds(pollInterval.Text) * 1000;

        try
        {
            store.Save(config);
            store.StartsWithWindows = startup.Checked;
        }
        catch (Exception error)
        {
            ModernDialog.Show(this, "Settings were not saved", error.Message, false);
            return;
        }

        Saved = true;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void PresetChanged(object? sender, EventArgs eventArgs) => ApplyPreset(preset.Text);

    private void ApplyPreset(string value)
    {
        // Presets stay conservative; task titles are always an explicit opt-in.
        showTaskTitle.Checked = false;
        showProject.Checked = true;
        showFile.Checked = value != "minimal";
        showTimer.Checked = true;
        fileMode.Text = value == "minimal" ? "name" : "relative";
        UpdatePrivacyPreview();
    }

    private void UpdatePrivacyPreview()
    {
        privacyPreview.Language = Languages.FirstOrDefault(item => item.Label == language.Text).Value ?? "en";
        privacyPreview.ShowTaskTitle = showTaskTitle.Checked;
        privacyPreview.ShowProject = showProject.Checked;
        privacyPreview.ShowFile = showFile.Checked;
        privacyPreview.ShowTimer = showTimer.Checked;
        privacyPreview.FileMode = fileMode.Text;
        privacyPreview.Invalidate();
    }

    private static int ParseSeconds(string value) => int.TryParse(value.Split(' ')[0], out var seconds) ? seconds : 7;

    private sealed class RemoteRow
    {
        public string Name { get; set; } = "Remote";
        public string Host { get; set; } = "";
        public string Roots { get; set; } = "";

        public RemoteHostConfig ToConfig() => new()
        {
            Name = Name.Trim(),
            Host = Host.Trim(),
            Roots = Roots.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        };

        public static RemoteRow FromConfig(RemoteHostConfig value) => new()
        {
            Name = value.Name,
            Host = value.Host,
            Roots = string.Join("; ", value.Roots),
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            remoteActionCancellation?.Cancel();
            navigationMotion?.Dispose();
        }
        base.Dispose(disposing);
    }
}
