namespace CodexPresence;

/// <summary>
/// Dark themed replacement for <see cref="MessageBox"/>.
///
/// The previous dialog clipped its message to a fixed 388x78 label, so
/// multi-line SSH output — exactly the case it exists for — was cut off with no
/// way to read or copy the rest. This one scrolls, selects and copies, and is
/// laid out by the layout engine rather than by hand-tuned pixel offsets.
/// </summary>
public sealed class ModernDialog : ModernForm
{
    private enum DialogTone { Success, Failure, Question }

    private ModernDialog(string titleText, string body, DialogTone tone, bool confirm)
        : base(titleText, MeasureSize(body))
    {
        CloseOnEscape = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;

        var header = new Panel { Dock = DockStyle.Top, Height = 68, BackColor = Visuals.Background };
        var toneIcon = new IconView(tone switch { DialogTone.Success => UiIcon.Check, DialogTone.Question => UiIcon.Info, _ => UiIcon.Warning })
        {
            Location = new Point(24, 22),
            Size = new Size(22, 22),
            IconColor = tone switch { DialogTone.Success => Visuals.Success, DialogTone.Question => Visuals.TextSecondary, _ => Visuals.Danger },
        };
        var status = new StatusPill
        {
            Text = tone switch { DialogTone.Success => "Ready", DialogTone.Question => "Confirm", _ => "Needs attention" },
            DotColor = tone switch { DialogTone.Success => Visuals.Success, DialogTone.Question => Visuals.Accent, _ => Visuals.Danger },
            FillColor = tone switch { DialogTone.Success => Visuals.SuccessSurface, DialogTone.Question => Visuals.SurfaceRaised, _ => Visuals.DangerSurface },
            Location = new Point(56, 19),
            IsLive = tone == DialogTone.Success,
        };
        header.Controls.AddRange([toneIcon, status]);

        var content = new Panel { Dock = DockStyle.Fill, BackColor = Visuals.Background, Padding = new Padding(24, 0, 24, 8) };
        var surface = new RoundedPanel { Dock = DockStyle.Fill, Radius = 10, BackColor = Visuals.Surface, Padding = new Padding(14, 12, 8, 12) };

        // A read-only text box keeps long diagnostics scrollable and copyable.
        var message = new TextBox
        {
            Dock = DockStyle.Fill,
            Text = body.Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Visuals.Surface,
            ForeColor = Visuals.TextSecondary,
            Font = Visuals.Font(9f),
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            TabStop = true,
            AccessibleName = "Message details",
        };
        surface.Controls.Add(message);
        content.Controls.Add(surface);

        var accept = Visuals.Button(confirm ? "Install update" : "Done", ButtonKind.Primary, UiIcon.Check);
        accept.Size = new Size(confirm ? 152 : 120, 42);
        accept.Margin = new Padding(8, 0, 0, 0);
        accept.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };

        var cancel = Visuals.Button("Not now", ButtonKind.Ghost);
        cancel.Size = new Size(108, 42);
        cancel.Margin = new Padding(0);

        var copy = Visuals.Button("Copy", ButtonKind.Ghost, UiIcon.Copy);
        copy.Size = new Size(92, 42);
        copy.Margin = new Padding(0);
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(body); }
            catch (Exception error) { ModernDialog.Show(this, "Could not copy the message", error.Message, false); }
        };

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            BackColor = Visuals.Canvas,
            Padding = new Padding(0, 15, 24, 0),
        };
        actions.Controls.Add(accept);
        if (confirm)
        {
            cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
            actions.Controls.Add(cancel);
        }

        var secondary = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            BackColor = Visuals.Canvas,
            Padding = new Padding(24, 15, 0, 0),
        };
        if (!confirm) secondary.Controls.Add(copy);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 72, BackColor = Visuals.Canvas };
        footer.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Visuals.BorderSoft });
        footer.Controls.Add(actions);
        footer.Controls.Add(secondary);

        ContentHost.Controls.Add(content);
        ContentHost.Controls.Add(footer);
        ContentHost.Controls.Add(header);

        AcceptButton = accept;
        if (confirm) CancelButton = cancel;
        Shown += (_, _) => accept.Focus();
    }

    /// <summary>Grows the window with the message instead of clipping it.</summary>
    private static Size MeasureSize(string body)
    {
        var lines = body.Replace("\r\n", "\n").Split('\n').Sum(line => 1 + line.Length / 62);
        return new Size(520, Math.Clamp(196 + lines * 18, 260, 470));
    }

    private static ModernDialog Create(string title, string body, DialogTone tone, bool confirm) =>
        new(title, string.IsNullOrWhiteSpace(body) ? "No further details were reported." : body, tone, confirm);

    public static void Show(IWin32Window owner, string title, string body, bool success)
    {
        using var dialog = Create(title, body, success ? DialogTone.Success : DialogTone.Failure, confirm: false);
        dialog.ShowDialog(owner);
    }

    public static void Show(string title, string body, bool success)
    {
        using var dialog = Create(title, body, success ? DialogTone.Success : DialogTone.Failure, confirm: false);
        dialog.StartPosition = FormStartPosition.CenterScreen;
        dialog.ShowDialog();
    }

    public static bool Confirm(string title, string body)
    {
        using var dialog = Create(title, body, DialogTone.Question, confirm: true);
        dialog.StartPosition = FormStartPosition.CenterScreen;
        return dialog.ShowDialog() == DialogResult.OK;
    }
}
