using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CodexPresence;

public sealed partial class SettingsForm : ModernForm
{
    private const long MaxTransitionPixels = 2_500_000;

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
    private readonly AccessibleStatusLabel remoteActionStatus = new();
    private readonly PageTransitionHost pageHost = new();
    private readonly Panel navigationIndicator = new() { Size = new Size(2, 24), BackColor = Visuals.Text };
    private readonly DiscordCardPreview privacyPreview = new() { Height = 188 };
    private readonly List<SettingsNavigationItem> navigation = [];
    private readonly List<ModernButton> remoteActions = [];
    private readonly Dictionary<SettingsNavigationItem, Control> pages = [];
    private IDisposable? navigationMotion;
    private CancellationTokenSource? remoteActionCancellation;
    private SettingsNavigationItem? selectedNavigation;

    public bool Saved { get; private set; }

    public SettingsForm(ConfigStore store, RemoteService remoteService) : base("Settings", new Size(920, 660), resizable: true)
    {
        this.store = store;
        this.remoteService = remoteService;
        config = store.Load();
        MinimumSize = new Size(820, 580);
        CloseOnEscape = true;

        var sidebar = new Panel { Dock = DockStyle.Left, Width = 194, BackColor = Visuals.Canvas };
        var section = Visuals.Eyebrow("Preferences");
        section.Location = new Point(20, 18);
        sidebar.Controls.Add(section);
        AddNavigation(sidebar, "General", UiIcon.General, 52, BuildGeneralPage);
        AddNavigation(sidebar, "Privacy", UiIcon.Privacy, 96, BuildPrivacyPage);
        AddNavigation(sidebar, "SSH workspaces", UiIcon.Remote, 140, BuildRemotePage);
        navigationIndicator.Location = new Point(14, 60);
        sidebar.Controls.Add(navigationIndicator);
        navigationIndicator.BringToFront();

        var localNote = new Panel { Size = new Size(166, 92), Anchor = AnchorStyles.Left | AnchorStyles.Bottom, BackColor = Visuals.Canvas };
        var shieldIcon = new IconView(UiIcon.Privacy) { Location = new Point(14, 13), Size = new Size(18, 18), IconColor = Visuals.Success };
        var shield = Visuals.Label("Local-first", 9, false, FontStyle.Bold);
        shield.Location = new Point(40, 14);
        var note = Visuals.Label("Task titles stay private\nuntil you share them. Tokens\nnever leave this device.", 8, true);
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
        privacyPreview.Radius = 0;
        privacyPreview.BorderWidth = 0;
        privacyPreview.BackColor = Visuals.Background;
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
        var button = new SettingsNavigationItem(text, icon);
        button.SetBounds(14, y, 166, 38);
        button.Activated += animate => ShowPage(button, pageFactory, animate);
        navigation.Add(button);
        parent.Controls.Add(button);
    }

    private void ShowPage(SettingsNavigationItem selected, Func<Control> pageFactory, bool animate = false)
    {
        if (ReferenceEquals(selectedNavigation, selected)) return;
        selectedNavigation = selected;
        foreach (var item in navigation) item.Selected = ReferenceEquals(item, selected);

        navigationMotion?.Dispose();
        var start = navigationIndicator.Top;
        var target = selected.Top + (selected.Height - navigationIndicator.Height) / 2;
        if (!animate || MotionClock.IsReduced)
        {
            navigationIndicator.Top = target;
            navigationIndicator.Invalidate();
        }
        else
        {
            navigationMotion = MotionClock.Animate(navigationIndicator, 180, value =>
            {
                navigationIndicator.Top = (int)Math.Round(MotionClock.Lerp(start, target, value));
                navigationIndicator.Invalidate();
            }, MotionEasing.EaseInOutCubic);
        }

        if (!pages.TryGetValue(selected, out var page))
        {
            page = pageFactory();
            page.Dock = DockStyle.Fill;
            pages[selected] = page;
            pageHost.AddPage(page);
        }
        pageHost.ShowPage(page, animate);
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
            Padding = new Padding(this.Dp(34), 0, this.Dp(24), this.Dp(24)),
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));

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
            card.Margin = Padding.Empty;
            settingsColumn.Controls.Add(card);
        }
        settingsColumn.Resize += (_, _) =>
        {
            var width = Math.Max(this.Dp(260), settingsColumn.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - this.Dp(4));
            foreach (Control card in settingsColumn.Controls) card.Width = width;
        };

        var previewColumn = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Visuals.Background,
            Padding = new Padding(this.Dp(12), 0, 0, 0),
            Margin = Padding.Empty,
        };
        privacyPreview.Height = this.Dp(188);
        privacyPreview.Margin = Padding.Empty;
        var previewNote = Visuals.Label("The preview stays visible while you change what Discord can see.", 8.25f, true);
        previewNote.Margin = new Padding(this.Dp(2), this.Dp(12), this.Dp(2), 0);
        previewColumn.Controls.AddRange([privacyPreview, previewNote]);
        previewColumn.Resize += (_, _) =>
        {
            var width = Math.Max(this.Dp(200), previewColumn.ClientSize.Width - previewColumn.Padding.Horizontal);
            privacyPreview.Width = width;
            previewNote.MaximumSize = new Size(width - this.Dp(4), 0);
        };

        content.Controls.Add(settingsColumn, 0, 0);
        content.Controls.Add(previewColumn, 1, 0);
        page.Controls.Add(content);
        page.Controls.Cast<Control>().FirstOrDefault(control => control.Dock == DockStyle.Top)?.BringToFront();
        return page;
    }

    private Panel PrivacyToggleGroup()
    {
        var group = new Panel { BackColor = Visuals.Background, Height = this.Dp(275) };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            ColumnCount = 1,
            BackColor = Visuals.Background,
            Margin = Padding.Empty,
        };
        for (var index = 0; index < 4; index++) table.RowStyles.Add(new RowStyle(SizeType.Absolute, this.Dp(68)));

        var toggles = new[] { showTaskTitle, showProject, showFile, showTimer };
        for (var index = 0; index < toggles.Length; index++)
        {
            var row = toggles[index];
            row.Dock = DockStyle.Fill;
            row.Margin = Padding.Empty;
            row.Radius = 0;
            row.BorderWidth = 0;
            foreach (var toggle in row.Controls.OfType<ToggleSwitch>()) toggle.Size = new Size(this.Dp(42), this.Dp(24));
            table.Controls.Add(row, 0, index);
        }
        group.Controls.Add(table);
        return group;
    }

    private Control BuildRemotePage()
    {
        var page = Page("SSH workspaces", "Map remote roots to servers. The most specific root wins.");
        ConfigureGrid();

        var grid = new Panel { Height = this.Dp(262), BackColor = Visuals.Background };
        remotes.Dock = DockStyle.Fill;
        grid.Controls.Add(remotes);
        remoteEmptyState.BackColor = Visuals.Surface;
        remoteEmptyState.Visible = remoteRows.Count == 0;
        grid.Controls.Add(remoteEmptyState);
        remoteEmptyState.BringToFront();
        grid.Resize += (_, _) => remoteEmptyState.Location = new Point(this.Dp(18), this.Dp(62));

        var actions = new Panel { Height = this.Dp(80), BackColor = Visuals.Background };
        remoteActions.Clear();
        AddRemoteAction(actions, "Add workspace", ButtonKind.Secondary, UiIcon.Add, 0, 148, (_, _) => remoteRows.Add(new RemoteRow()));
        AddRemoteAction(actions, "Remove", ButtonKind.Ghost, UiIcon.Remove, 156, 108, (_, _) =>
        {
            if (remotes.CurrentRow?.DataBoundItem is RemoteRow row) remoteRows.Remove(row);
        });
        AddRemoteAction(actions, "Test SSH", ButtonKind.Secondary, UiIcon.Remote, 272, 120, async (_, _) => await RunRemoteAction(false));
        AddRemoteAction(actions, "Install helper", ButtonKind.Primary, UiIcon.Check, 400, 140, async (_, _) => await RunRemoteAction(true));
        remoteActionStatus.AutoSize = false;
        remoteActionStatus.AccessibleRole = AccessibleRole.StatusBar;
        remoteActionStatus.Visible = false;
        actions.Controls.Add(remoteActionStatus);
        actions.Resize += (_, _) => remoteActionStatus.SetBounds(this.Dp(4), this.Dp(58), Math.Max(this.Dp(180), actions.Width - this.Dp(8)), this.Dp(20));

        Stack(page, grid, actions, FieldCard("Refresh interval", "How often the selected remote task is checked", pollInterval));
        return page;
    }

    private void AddRemoteAction(Control parent, string text, ButtonKind kind, UiIcon? icon, int x, int width, EventHandler onClick)
    {
        var button = Visuals.Button(text, kind, icon);
        button.SetBounds(this.Dp(x), this.Dp(8), this.Dp(width), this.Dp(40));
        button.Click += onClick;
        remoteActions.Add(button);
        parent.Controls.Add(button);
    }

    private Panel Page(string titleText, string subtitleText)
    {
        var page = new Panel { BackColor = Visuals.Background };
        var header = new Panel { Dock = DockStyle.Top, Height = this.Dp(88), BackColor = Visuals.Background };
        var title = Visuals.Heading(titleText, 20);
        title.Location = new Point(this.Dp(34), this.Dp(26));
        var subtitle = Visuals.Label(subtitleText, 9, true);
        subtitle.Location = new Point(this.Dp(35), this.Dp(58));
        header.Controls.AddRange([title, subtitle]);
        page.Controls.Add(header);
        return page;
    }

    /// <summary>
    /// Lays cards out in a scrollable column. The previous build positioned
    /// every row with hard-coded pixel bounds, which broke as soon as the
    /// window or the system scaling changed.
    /// </summary>
    private void Stack(Control page, params Control[] cards)
    {
        var column = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Visuals.Background,
            Padding = new Padding(this.Dp(34), 0, this.Dp(24), this.Dp(24)),
        };
        foreach (var card in cards)
        {
            card.Margin = Padding.Empty;
            column.Controls.Add(card);
        }
        column.Resize += (_, _) =>
        {
            var width = Math.Max(this.Dp(420), column.ClientSize.Width - column.Padding.Horizontal);
            foreach (Control card in column.Controls) card.Width = width;
        };
        page.Controls.Add(column);
        page.Controls.Cast<Control>().FirstOrDefault(control => control.Dock == DockStyle.Top)?.BringToFront();
    }

    private Panel FieldCard(string titleText, string descriptionText, Control input)
    {
        var card = new Panel { BackColor = Visuals.Background, Height = this.Dp(74) };
        var title = Visuals.Label(titleText, 10, false, FontStyle.Bold);
        title.Location = new Point(this.Dp(16), this.Dp(15));
        var description = Visuals.Label(descriptionText, 8.5f, true);
        description.Location = new Point(this.Dp(16), this.Dp(40));
        if (input.Parent is null && this.Scale() > 1f)
        {
            input.Size = new Size(this.Dp(input.Width), this.Dp(input.Height));
        }
        input.AccessibleName = titleText;
        input.AccessibleDescription = descriptionText;
        input.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        card.Controls.AddRange([title, description, input]);
        card.Paint += (_, e) =>
        {
            using var rule = new Pen(Visuals.BorderSoft, Math.Max(1f, card.Scale()));
            e.Graphics.DrawLine(rule, 0, card.Height - 1, card.Width, card.Height - 1);
        };
        card.Resize += (_, _) =>
        {
            input.Location = new Point(card.Width - input.Width - this.Dp(16), (card.Height - input.Height) / 2);
            var textWidth = Math.Max(this.Dp(100), input.Left - this.Dp(32));
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
        remotes.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(RemoteRow.Name), HeaderText = "Name", Width = this.Dp(135) });
        remotes.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(RemoteRow.Host), HeaderText = "User@host", Width = this.Dp(180) });
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

    private sealed class SettingsNavigationItem : Panel
    {
        private readonly ModernButton button;
        private bool selected;

        public event Action<bool>? Activated;

        public bool Selected
        {
            get => selected;
            set
            {
                if (selected == value) return;
                selected = value;
                button.Kind = selected ? ButtonKind.Secondary : ButtonKind.Ghost;
                AccessibleDescription = selected ? "Selected settings page" : "Settings page";
                if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            }
        }

        public SettingsNavigationItem(string text, UiIcon icon)
        {
            BackColor = Visuals.Canvas;
            Cursor = Cursors.Hand;
            Padding = new Padding(1);
            TabStop = true;
            AccessibleRole = AccessibleRole.PageTab;
            AccessibleName = text;
            AccessibleDescription = "Settings page";
            SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint, true);

            button = Visuals.Button(text, ButtonKind.Ghost, icon);
            button.Dock = DockStyle.Fill;
            button.Radius = 5;
            button.TextAlign = ContentAlignment.MiddleLeft;
            button.TabStop = false;
            button.AccessibleRole = AccessibleRole.None;
            button.Click += (_, _) => Activate(animate: true);
            Controls.Add(button);
        }

        protected override bool IsInputKey(Keys keyData) => keyData is Keys.Space or Keys.Enter || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Space or Keys.Enter)
            {
                Activate(animate: false);
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnEnter(EventArgs e)
        {
            BackColor = Visuals.FocusRing;
            base.OnEnter(e);
        }

        protected override void OnLeave(EventArgs e)
        {
            BackColor = Visuals.Canvas;
            base.OnLeave(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            button.Enabled = Enabled;
            base.OnEnabledChanged(e);
        }

        protected override AccessibleObject CreateAccessibilityInstance() => new NavigationAccessibleObject(this);

        private void Activate(bool animate)
        {
            if (!Enabled) return;
            Focus();
            Activated?.Invoke(animate);
        }

        private sealed class NavigationAccessibleObject(SettingsNavigationItem owner) : ControlAccessibleObject(owner)
        {
            public override string? DefaultAction => "Open";
            public override AccessibleStates State => base.State |
                AccessibleStates.Selectable |
                (owner.Selected ? AccessibleStates.Selected : AccessibleStates.None);

            public override int GetChildCount() => 0;
            public override void DoDefaultAction() => owner.Activate(animate: false);
        }
    }

    /// <summary>
    /// Keeps real pages docked at every point in the transition. A temporary
    /// paint-only overlay carries the slide, so rapid navigation or a resize
    /// cannot strand a page with stale bounds.
    /// </summary>
    private sealed class PageTransitionHost : Panel
    {
        private const int TransitionDurationMs = 180;
        private const int TravelDp = 12;

        private readonly PageTransitionOverlay overlay = new();
        private IDisposable? motion;
        private Control? currentPage;

        public PageTransitionHost()
        {
            Dock = DockStyle.Fill;
            BackColor = Visuals.Background;
            Controls.Add(overlay);
            Resize += (_, _) => CompleteTransition();
        }

        public void AddPage(Control page)
        {
            ArgumentNullException.ThrowIfNull(page);
            page.Dock = DockStyle.Fill;
            page.Visible = false;
            Controls.Add(page);
            overlay.BringToFront();
        }

        public void ShowPage(Control page, bool animate)
        {
            ArgumentNullException.ThrowIfNull(page);
            if (ReferenceEquals(currentPage, page)) return;

            var shouldAnimate = animate && CanAnimate() && currentPage is not null;
            var outgoingFrame = shouldAnimate ? CaptureCurrentFrame() : null;
            CompleteTransition();

            if (currentPage is not null) currentPage.Visible = false;
            currentPage = page;
            page.Dock = DockStyle.Fill;
            page.Visible = true;
            page.BringToFront();
            overlay.BringToFront();
            PerformLayout();
            page.PerformLayout();

            if (outgoingFrame is null)
            {
                overlay.Visible = false;
                return;
            }

            var incomingFrame = CapturePage(page);
            if (incomingFrame is null)
            {
                outgoingFrame.Dispose();
                overlay.Visible = false;
                return;
            }

            overlay.SetFrames(outgoingFrame, incomingFrame, TravelDp);
            overlay.Visible = true;
            overlay.BringToFront();
            motion = MotionClock.Animate(
                overlay,
                TransitionDurationMs,
                value => overlay.Progress = value,
                MotionEasing.EaseOutCubic,
                completed: CompleteTransition);
        }

        private bool CanAnimate() =>
            IsHandleCreated &&
            Visible &&
            ClientSize.Width > 0 &&
            ClientSize.Height > 0 &&
            (long)ClientSize.Width * ClientSize.Height <= MaxTransitionPixels &&
            !DesignMode &&
            !MotionClock.IsReduced;

        private Bitmap? CaptureCurrentFrame() => overlay.Visible
            ? overlay.CaptureComposite()
            : currentPage is null ? null : CapturePage(currentPage);

        private Bitmap? CapturePage(Control page)
        {
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0 ||
                (long)ClientSize.Width * ClientSize.Height > MaxTransitionPixels) return null;

            Bitmap? frame = null;
            try
            {
                frame = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppPArgb);
                frame.SetResolution(DeviceDpi, DeviceDpi);
                page.DrawToBitmap(frame, new Rectangle(Point.Empty, frame.Size));
                return frame;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or ExternalException or OutOfMemoryException)
            {
                frame?.Dispose();
                return null;
            }
        }

        private void CompleteTransition()
        {
            motion?.Dispose();
            motion = null;
            overlay.ClearFrames();
            overlay.Visible = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) CompleteTransition();
            base.Dispose(disposing);
        }
    }

    private sealed class PageTransitionOverlay : Control
    {
        private Bitmap? outgoingFrame;
        private Bitmap? incomingFrame;
        private float progress;
        private int travelDp;

        public float Progress
        {
            get => progress;
            set
            {
                progress = Math.Clamp(value, 0f, 1f);
                Invalidate();
            }
        }

        public PageTransitionOverlay()
        {
            Dock = DockStyle.Fill;
            BackColor = Visuals.Background;
            TabStop = false;
            AccessibleRole = AccessibleRole.None;
            Visible = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        public void SetFrames(Bitmap outgoing, Bitmap incoming, int travel)
        {
            ArgumentNullException.ThrowIfNull(outgoing);
            ArgumentNullException.ThrowIfNull(incoming);
            ClearFrames();
            outgoingFrame = outgoing;
            incomingFrame = incoming;
            travelDp = Math.Max(0, travel);
            progress = 0f;
            Invalidate();
        }

        public Bitmap? CaptureComposite()
        {
            if (outgoingFrame is null || incomingFrame is null || Width <= 0 || Height <= 0 ||
                (long)Width * Height > MaxTransitionPixels) return null;

            Bitmap? frame = null;
            try
            {
                frame = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
                frame.SetResolution(DeviceDpi, DeviceDpi);
                using var graphics = Graphics.FromImage(frame);
                DrawComposite(graphics);
                return frame;
            }
            catch (Exception error) when (error is ArgumentException or ExternalException or OutOfMemoryException)
            {
                frame?.Dispose();
                return null;
            }
        }

        public void ClearFrames()
        {
            outgoingFrame?.Dispose();
            incomingFrame?.Dispose();
            outgoingFrame = null;
            incomingFrame = null;
            progress = 1f;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            DrawComposite(e.Graphics);
        }

        private void DrawComposite(Graphics graphics)
        {
            graphics.Clear(BackColor);
            if (outgoingFrame is null || incomingFrame is null) return;

            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var travelPixels = this.Dp(travelDp);
            // Page captures are opaque. Keeping the outgoing frame opaque and
            // fading the incoming one over it produces a true crossfade;
            // fading both would leak the canvas color through at mid-frame.
            DrawFrame(graphics, outgoingFrame, -travelPixels * progress * .35f, 1f);
            DrawFrame(graphics, incomingFrame, travelPixels * (1f - progress), progress);
        }

        private static void DrawFrame(Graphics graphics, Image image, float offsetX, float opacity)
        {
            if (opacity <= 0f) return;
            using var attributes = new ImageAttributes();
            var matrix = new ColorMatrix { Matrix33 = Math.Clamp(opacity, 0f, 1f) };
            attributes.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
            graphics.DrawImage(
                image,
                new Rectangle((int)Math.Round(offsetX), 0, image.Width, image.Height),
                0f,
                0f,
                image.Width,
                image.Height,
                GraphicsUnit.Pixel,
                attributes);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ClearFrames();
            base.Dispose(disposing);
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
        if (disposing)
        {
            remoteActionCancellation?.Cancel();
            navigationMotion?.Dispose();
        }
        base.Dispose(disposing);
    }
}
