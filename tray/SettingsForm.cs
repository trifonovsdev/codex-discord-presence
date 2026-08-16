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
    private readonly ToggleRow showTaskTitle = new("Task title", "Share the selected Codex task title when it is available");
    private readonly ToggleRow showProject = new("Project name", "Share the active workspace on your Discord card");
    private readonly ToggleRow showFile = new("Edited file", "Share the latest file touched in the selected task");
    private readonly ToggleRow showTimer = new("Session timer", "Use one stable timer for the whole Codex session");
    private readonly ModernSelect fileMode = Visuals.Select(["name", "relative"], 126);
    private readonly ModernSelect pollInterval = Visuals.Select(["3 seconds", "5 seconds", "7 seconds", "10 seconds", "15 seconds", "30 seconds"], 170);
    private readonly BindingList<RemoteRow> remoteRows = [];
    private readonly DataGridView remotes = new();
    private readonly Label remoteEmptyState = Visuals.Label("No SSH workspaces yet. Add one to follow remote Codex tasks.", 9, true);
    private readonly AccessibleStatusLabel remoteActionStatus = new();
    private readonly Panel pageHost = new() { Dock = DockStyle.Fill, BackColor = Visuals.Background };
    private readonly Panel tabUnderline = new() { Height = 2, BackColor = Visuals.Text, Visible = false };
    private readonly List<ModernButton> tabs = [];
    private readonly List<ModernButton> remoteActions = [];
    private readonly Dictionary<ModernButton, Control> pages = [];
    private CancellationTokenSource? remoteActionCancellation;
    private ModernButton? selectedTab;

    public bool Saved { get; private set; }

    public SettingsForm(ConfigStore store, RemoteService remoteService) : base("Settings", new Size(800, 600), resizable: true)
    {
        this.store = store;
        this.remoteService = remoteService;
        config = store.Load();
        MinimumSize = SizeFromClientSize(new Size(620, 400));
        CloseOnEscape = true;

        var tabBar = BuildTabBar();
        var footer = BuildFooter();
        ContentHost.Controls.Add(pageHost);
        ContentHost.Controls.Add(footer);
        ContentHost.Controls.Add(tabBar);

        RegisterPage(tabs[0], BuildGeneralPage());
        RegisterPage(tabs[1], BuildPrivacyPage());
        RegisterPage(tabs[2], BuildRemotePage());

        preset.SelectedIndexChanged += PresetChanged;
        remoteRows.ListChanged += (_, _) => remoteEmptyState.Visible = remoteRows.Count == 0;
        FormClosing += (_, _) => remoteActionCancellation?.Cancel();
        LoadValues();
        ShowPage(tabs[0]);
    }

    private Control BuildTabBar()
    {
        var bar = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Visuals.Canvas };
        bar.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Visuals.BorderSoft });
        AddTab(bar, "General", 24, 96);
        AddTab(bar, "Privacy", 124, 96);
        AddTab(bar, "SSH workspaces", 224, 144);
        bar.Controls.Add(tabUnderline);
        tabUnderline.BringToFront();
        bar.Resize += (_, _) => LayoutTabUnderline();
        return bar;
    }

    private void AddTab(Control parent, string text, int x, int width)
    {
        var tab = Visuals.Button(text, ButtonKind.Ghost);
        tab.SetBounds(this.Dp(x), this.Dp(10), this.Dp(width), this.Dp(36));
        tab.AccessibleRole = AccessibleRole.PageTab;
        tab.AccessibleName = text;
        tab.Click += (_, _) => ShowPage(tab);
        tabs.Add(tab);
        parent.Controls.Add(tab);
    }

    private void RegisterPage(ModernButton tab, Control page)
    {
        page.Dock = DockStyle.Fill;
        page.Visible = false;
        pages.Add(tab, page);
        pageHost.Controls.Add(page);
    }

    private void ShowPage(ModernButton selected)
    {
        if (ReferenceEquals(selectedTab, selected)) return;
        selectedTab = selected;
        foreach (var tab in tabs)
        {
            var active = ReferenceEquals(tab, selected);
            tab.Kind = ButtonKind.Ghost;
            tab.IsSelected = active;
            tab.Font = Visuals.Font(9.25f, active ? FontStyle.Bold : FontStyle.Regular);
            tab.AccessibleDescription = active ? "Selected settings page" : "Settings page";
        }
        LayoutTabUnderline();

        var page = pages[selected];

        foreach (Control candidate in pageHost.Controls) candidate.Visible = ReferenceEquals(candidate, page);
        page.BringToFront();
        page.Focus();
    }

    private void LayoutTabUnderline()
    {
        if (selectedTab is null || tabUnderline.Parent is null) return;
        tabUnderline.SetBounds(
            selectedTab.Left + this.Dp(12),
            tabUnderline.Parent.ClientSize.Height - this.Dp(3),
            Math.Max(this.Dp(20), selectedTab.Width - this.Dp(24)),
            this.Dp(2));
        tabUnderline.Visible = true;
        tabUnderline.BringToFront();
    }

    private Panel BuildFooter()
    {
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = Visuals.Canvas };
        footer.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Visuals.BorderSoft });
        var save = Visuals.Button("Save changes", ButtonKind.Primary);
        save.SetBounds(0, 12, 142, 40);
        save.Click += SaveClicked;
        var cancel = Visuals.Button("Cancel", ButtonKind.Ghost);
        cancel.SetBounds(0, 12, 88, 40);
        cancel.Click += (_, _) => Close();
        footer.Controls.AddRange([cancel, save]);
        footer.Resize += (_, _) =>
        {
            save.Left = footer.Width - save.Width - footer.Dp(24);
            cancel.Left = save.Left - cancel.Width - footer.Dp(6);
        };
        return footer;
    }

    private Control BuildGeneralPage()
    {
        var page = Page("General", "Startup, updates, and how your Discord presence behaves.");
        Stack(page, enabled, startup, updates, SettingRow("Card language", "Language of the text published to Discord", language));
        return page;
    }

    private Control BuildPrivacyPage()
    {
        var page = Page("Privacy", "Choose exactly which Codex signals Discord receives.");
        Stack(
            page,
            SettingRow("Privacy preset", "Quick baseline; task titles always require explicit opt-in", preset),
            PrivacyToggleGroup(),
            SettingRow("File display", "Filename only or repository-relative path", fileMode));
        return page;
    }

    private Panel PrivacyToggleGroup()
    {
        var group = new Panel { BackColor = Visuals.Background, Height = this.Dp(256) };
        var toggles = new[] { showTaskTitle, showProject, showFile, showTimer };
        foreach (var row in toggles)
        {
            row.Height = this.Dp(64);
            row.Radius = 0;
            row.BorderWidth = 0;
            row.Margin = Padding.Empty;
            foreach (var toggle in row.Controls.OfType<ToggleSwitch>())
                toggle.Size = new Size(this.Dp(42), this.Dp(24));
            group.Controls.Add(row);
        }
        group.Resize += (_, _) =>
        {
            for (var index = 0; index < toggles.Length; index++)
            {
                toggles[index].SetBounds(0, index * this.Dp(64), group.Width, this.Dp(64));
            }
        };
        return group;
    }

    private Control BuildRemotePage()
    {
        var page = Page("SSH workspaces", "Map remote roots to servers. The most specific root wins.");
        ConfigureGrid();

        var grid = new Panel { Height = this.Dp(250), BackColor = Visuals.Background };
        remotes.Dock = DockStyle.Fill;
        grid.Controls.Add(remotes);
        remoteEmptyState.BackColor = Visuals.Surface;
        remoteEmptyState.Visible = remoteRows.Count == 0;
        grid.Controls.Add(remoteEmptyState);
        remoteEmptyState.BringToFront();
        grid.Resize += (_, _) => remoteEmptyState.SetBounds(this.Dp(18), this.Dp(62), Math.Max(this.Dp(220), grid.Width - this.Dp(36)), this.Dp(24));

        var actions = new Panel { Height = this.Dp(74), BackColor = Visuals.Background };
        remoteActions.Clear();
        AddRemoteAction(actions, "Add", ButtonKind.Secondary, UiIcon.Add, 0, 90, (_, _) => remoteRows.Add(new RemoteRow()));
        AddRemoteAction(actions, "Remove", ButtonKind.Ghost, UiIcon.Remove, 96, 102, (_, _) =>
        {
            if (remotes.CurrentRow?.DataBoundItem is RemoteRow row) remoteRows.Remove(row);
        });
        AddRemoteAction(actions, "Test SSH", ButtonKind.Secondary, UiIcon.Remote, 204, 112, async (_, _) => await RunRemoteAction(false));
        AddRemoteAction(actions, "Install helper", ButtonKind.Secondary, UiIcon.Check, 322, 132, async (_, _) => await RunRemoteAction(true));
        remoteActionStatus.AutoSize = false;
        remoteActionStatus.AccessibleRole = AccessibleRole.StatusBar;
        remoteActionStatus.Visible = false;
        actions.Controls.Add(remoteActionStatus);
        actions.Resize += (_, _) => remoteActionStatus.SetBounds(this.Dp(4), this.Dp(52), Math.Max(this.Dp(180), actions.Width - this.Dp(8)), this.Dp(20));

        Stack(page, grid, actions, SettingRow("Refresh interval", "How often the selected remote task is checked", pollInterval));
        return page;
    }

    private void AddRemoteAction(Control parent, string text, ButtonKind kind, UiIcon? icon, int x, int width, EventHandler onClick)
    {
        var button = Visuals.Button(text, kind, icon);
        button.SetBounds(this.Dp(x), this.Dp(6), this.Dp(width), this.Dp(38));
        button.Click += onClick;
        remoteActions.Add(button);
        parent.Controls.Add(button);
    }

    private Panel Page(string titleText, string subtitleText)
    {
        var page = new Panel { BackColor = Visuals.Background };
        var header = new Panel { Dock = DockStyle.Top, Height = this.Dp(82), BackColor = Visuals.Background };
        var title = Visuals.Heading(titleText, 20);
        title.AutoSize = false;
        var subtitle = Visuals.Label(subtitleText, 9, true);
        subtitle.AutoSize = false;
        subtitle.AutoEllipsis = true;
        header.Controls.AddRange([title, subtitle]);
        header.Resize += (_, _) =>
        {
            var width = Math.Min(this.Dp(660), Math.Max(this.Dp(420), header.Width - this.Dp(48)));
            var left = Math.Max(this.Dp(24), (header.Width - width) / 2);
            title.SetBounds(left, this.Dp(19), width, this.Dp(30));
            subtitle.SetBounds(left, this.Dp(52), width, this.Dp(21));
        };
        page.Controls.Add(header);
        return page;
    }

    private void Stack(Control page, params Control[] rows)
    {
        var column = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Visuals.Background,
            Padding = Padding.Empty,
        };
        foreach (var row in rows)
        {
            row.Margin = Padding.Empty;
            column.Controls.Add(row);
        }
        column.Resize += (_, _) =>
        {
            var available = column.ClientSize.Width - (column.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0);
            var width = Math.Min(this.Dp(660), Math.Max(this.Dp(420), available - this.Dp(48)));
            var left = Math.Max(this.Dp(24), (available - width) / 2);
            foreach (Control row in column.Controls)
            {
                row.Width = width;
                row.Margin = new Padding(left, 0, 0, 0);
            }
        };
        page.Controls.Add(column);
        page.Controls.Cast<Control>().FirstOrDefault(control => control.Dock == DockStyle.Top)?.BringToFront();
    }

    private Panel SettingRow(string titleText, string descriptionText, Control input)
    {
        var row = new Panel { BackColor = Visuals.Background, Height = this.Dp(64) };
        var title = Visuals.Label(titleText, 10, false, FontStyle.Bold);
        title.AutoSize = false;
        title.AutoEllipsis = true;
        var description = Visuals.Label(descriptionText, 8.5f, true);
        description.AutoSize = false;
        description.AutoEllipsis = true;
        input.AccessibleName = titleText;
        input.AccessibleDescription = descriptionText;
        row.Controls.AddRange([title, description, input]);
        row.Paint += (_, e) =>
        {
            using var rule = new Pen(Visuals.BorderSoft);
            e.Graphics.DrawLine(rule, 0, row.Height - 1, row.Width, row.Height - 1);
        };
        row.Resize += (_, _) =>
        {
            input.Location = new Point(row.Width - input.Width - this.Dp(12), (row.Height - input.Height) / 2);
            var textWidth = Math.Max(this.Dp(120), input.Left - this.Dp(24));
            title.SetBounds(this.Dp(12), this.Dp(10), textWidth, this.Dp(22));
            description.SetBounds(this.Dp(12), this.Dp(34), textWidth, this.Dp(20));
        };
        return row;
    }

    private void ConfigureGrid()
    {
        if (remotes.Columns.Count > 0) return;
        remotes.BackgroundColor = Visuals.Surface;
        remotes.GridColor = Visuals.BorderSoft;
        remotes.BorderStyle = BorderStyle.None;
        remotes.EnableHeadersVisualStyles = false;
        remotes.ColumnHeadersHeight = this.Dp(40);
        remotes.RowTemplate.Height = this.Dp(42);
        remotes.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        remotes.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        remotes.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Visuals.SurfaceRaised,
            ForeColor = Visuals.TextSecondary,
            SelectionBackColor = Visuals.SurfaceRaised,
            Font = Visuals.Font(8.5f, FontStyle.Bold),
            Padding = new Padding(this.Dp(8), 0, 0, 0),
            Alignment = DataGridViewContentAlignment.MiddleLeft,
        };
        remotes.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Visuals.Surface,
            ForeColor = Visuals.Text,
            SelectionBackColor = Visuals.SurfaceHover,
            SelectionForeColor = Visuals.Text,
            Font = Visuals.Font(9),
            Padding = new Padding(this.Dp(8), 0, 0, 0),
        };
        remotes.AutoGenerateColumns = false;
        remotes.AllowUserToAddRows = false;
        remotes.AllowUserToResizeRows = false;
        remotes.RowHeadersVisible = false;
        remotes.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        remotes.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(RemoteRow.Name), HeaderText = "Name", Width = this.Dp(130) });
        remotes.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(RemoteRow.Host), HeaderText = "User@host", Width = this.Dp(175) });
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
        SetRemoteActionStatus(install ? $"Installing helper on {row.Host}…" : $"Testing SSH connection to {row.Host}…", Visuals.TextSecondary);
        UseWaitCursor = true;
        try
        {
            var result = install
                ? await remoteService.InstallHelperAsync(row.ToConfig(), cancellation.Token)
                : await remoteService.TestAsync(row.ToConfig(), cancellation.Token);
            if (cancellation.IsCancellationRequested || IsDisposed || Disposing) return;
            SetRemoteActionStatus(
                result.Ok ? (install ? "Helper installed" : "SSH connection ready") : (install ? "Helper installation failed" : "SSH connection failed"),
                result.Ok ? Visuals.Success : Visuals.Danger);
            ModernDialog.Show(this, result.Ok ? "Connection ready" : "SSH failed", result.Output, result.Ok);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!IsDisposed && !Disposing) SetRemoteActionStatus("SSH action cancelled", Visuals.Muted);
        }
        catch (Exception error)
        {
            if (!IsDisposed && !Disposing)
            {
                SetRemoteActionStatus("SSH action failed", Visuals.Danger);
                ModernDialog.Show(this, "SSH action failed", error.Message, false);
            }
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

    private void SetRemoteActionStatus(string text, Color color)
    {
        remoteActionStatus.Text = text;
        remoteActionStatus.ForeColor = color;
        remoteActionStatus.AccessibleName = text;
        remoteActionStatus.Visible = true;
        remoteActionStatus.Announce();
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
        showTaskTitle.Checked = false;
        showProject.Checked = true;
        showFile.Checked = value != "minimal";
        showTimer.Checked = true;
        fileMode.Text = value == "minimal" ? "name" : "relative";
    }

    private static int ParseSeconds(string value) => int.TryParse(value.Split(' ')[0], out var seconds) ? seconds : 7;

    private sealed class AccessibleStatusLabel : Label
    {
        public AccessibleStatusLabel()
        {
            ForeColor = Visuals.TextSecondary;
            Font = Visuals.Font(8.5f, FontStyle.Bold);
            AutoSize = true;
            BackColor = Color.Transparent;
            UseMnemonic = false;
        }

        public void Announce()
        {
            if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
        }
    }

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
        if (disposing) remoteActionCancellation?.Cancel();
        base.Dispose(disposing);
    }
}
