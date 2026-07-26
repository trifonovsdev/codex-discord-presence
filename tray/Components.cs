using System.Drawing.Drawing2D;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace CodexPresence;

public enum ButtonKind { Primary, Secondary, Ghost, Danger }

public sealed class ModernButton : Button
{
    private bool hovered;
    private bool pressed;
    private ButtonKind kind = ButtonKind.Secondary;

    public ButtonKind Kind
    {
        get => kind;
        set { if (kind == value) return; kind = value; Invalidate(); }
    }

    public string? IconGlyph { get; set; }
    public int Radius { get; set; } = 10;

    public ModernButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = Visuals.Font(9.5f, FontStyle.Bold);
        Cursor = Cursors.Hand;
        TabStop = true;
        UseMnemonic = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnter(EventArgs e) { Invalidate(); base.OnEnter(e); }
    protected override void OnLeave(EventArgs e) { Invalidate(); base.OnLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Visuals.Background);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        var (background, foreground, border) = Colors();
        if (!Enabled) { background = Visuals.Surface; foreground = Visuals.Muted; border = Visuals.BorderSoft; }
        else if (pressed) background = ControlPaint.Dark(background, .08f);
        else if (hovered) background = Kind == ButtonKind.Primary ? Color.White : Visuals.SurfaceHover;

        using var fill = new SolidBrush(background);
        e.Graphics.FillRoundedRectangle(fill, bounds, Radius);
        if (border != Color.Transparent)
        {
            using var pen = new Pen(border);
            e.Graphics.DrawRoundedRectangle(pen, bounds, Radius);
        }

        var padding = this.Dp(12);
        var text = string.IsNullOrWhiteSpace(IconGlyph) ? Text : $"{IconGlyph}   {Text}";
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
            | (TextAlign == ContentAlignment.MiddleLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter);
        TextRenderer.DrawText(e.Graphics, text, Font, new Rectangle(padding, 0, Width - padding * 2, Height), foreground, flags);

        // A visible focus ring is the only way to drive this UI from the keyboard.
        if (Focused && ShowFocusCues)
        {
            var inset = this.Dp(3);
            using var focus = new Pen(Visuals.FocusRing, Math.Max(1f, this.Scale()));
            e.Graphics.DrawRoundedRectangle(focus, RectangleF.Inflate(bounds, -inset, -inset), Math.Max(2, Radius - inset));
        }
    }

    private (Color Background, Color Foreground, Color Border) Colors() => Kind switch
    {
        ButtonKind.Primary => (Visuals.Accent, Visuals.AccentText, Color.Transparent),
        ButtonKind.Ghost => (Color.Transparent, Visuals.TextSecondary, Color.Transparent),
        ButtonKind.Danger => (Visuals.DangerSurface, Visuals.Danger, Color.Transparent),
        _ => (Visuals.SurfaceRaised, Visuals.Text, Visuals.Border),
    };
}

/// <summary>
/// A rounded surface. The corners are painted rather than clipped with a
/// <see cref="Region"/>: regions are not antialiased, which is what made every
/// card in the previous build show visibly stair-stepped corners.
/// </summary>
public class RoundedPanel : Panel
{
    public int Radius { get; set; } = 14;
    public Color BorderColor { get; set; } = Visuals.BorderSoft;
    public int BorderWidth { get; set; } = 1;

    public RoundedPanel()
    {
        BackColor = Visuals.Surface;
        Margin = Padding.Empty;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Visuals.Background);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillRoundedRectangle(brush, new RectangleF(0, 0, Width, Height), Radius);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (BorderWidth <= 0) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(BorderColor, BorderWidth);
        e.Graphics.DrawRoundedRectangle(pen, new RectangleF(.5f, .5f, Width - 1.5f, Height - 1.5f), Radius);
    }
}

public sealed class ToggleSwitch : Control
{
    private readonly System.Windows.Forms.Timer animation = new() { Interval = 15 };
    private float progress;
    private bool isChecked;
    private bool hovered;

    public bool Checked
    {
        get => isChecked;
        set
        {
            if (isChecked == value) return;
            isChecked = value;
            animation.Start();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? CheckedChanged;

    public ToggleSwitch()
    {
        Size = new Size(42, 24);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.CheckButton;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        animation.Tick += (_, _) =>
        {
            var target = isChecked ? 1f : 0f;
            progress += Math.Sign(target - progress) * .2f;
            if (Math.Abs(target - progress) < .01f) { progress = target; animation.Stop(); }
            Invalidate();
        };
    }

    public override string ToString() => $"{Name}: {(isChecked ? "on" : "off")}";
    protected override AccessibleObject CreateAccessibilityInstance() => new ToggleAccessibleObject(this);

    protected override void OnClick(EventArgs e) { Checked = !Checked; Focus(); base.OnClick(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) { Checked = !Checked; e.Handled = true; }
        base.OnKeyDown(e);
    }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnter(EventArgs e) { Invalidate(); base.OnEnter(e); }
    protected override void OnLeave(EventArgs e) { Invalidate(); base.OnLeave(e); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Space or Keys.Enter || base.IsInputKey(keyData);

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var off = hovered ? Visuals.SurfaceHover : Visuals.Border;
        var on = hovered ? Color.FromArgb(84, 230, 177) : Visuals.Success;
        using var trackBrush = new SolidBrush(Blend(off, on, progress));
        e.Graphics.FillRoundedRectangle(trackBrush, new RectangleF(0, 1, Width, Height - 2), Height / 2f);

        var margin = this.Dp(4);
        var diameter = Height - margin * 2;
        var travel = Width - diameter - margin * 2;
        using var thumb = new SolidBrush(Blend(Visuals.TextSecondary, Color.FromArgb(10, 35, 28), progress));
        e.Graphics.FillEllipse(thumb, margin + travel * progress, margin, diameter, diameter);

        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(Visuals.FocusRing, Math.Max(1f, this.Scale()));
            e.Graphics.DrawRoundedRectangle(focus, new RectangleF(-1.5f, -0.5f, Width + 2, Height - 1), Height / 2f);
        }
    }

    private static Color Blend(Color from, Color to, float amount)
    {
        var t = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            (int)(from.R + (to.R - from.R) * t),
            (int)(from.G + (to.G - from.G) * t),
            (int)(from.B + (to.B - from.B) * t));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) animation.Dispose();
        base.Dispose(disposing);
    }

    private sealed class ToggleAccessibleObject(ToggleSwitch owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleStates State => owner.Checked
            ? base.State | AccessibleStates.Checked
            : base.State;
    }
}

public sealed class ModernSelect : Control
{
    private readonly List<string> options;
    private readonly ContextMenuStrip menu;
    private string selected = "";
    private bool hovered;

    public IReadOnlyList<string> Options => options;

    [AllowNull]
    public override string Text
    {
        get => selected;
        set
        {
            var next = options.FirstOrDefault(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase)) ?? value ?? "";
            if (selected == next) return;
            selected = next;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? SelectedIndexChanged;

    public ModernSelect(IEnumerable<string> values)
    {
        options = values.ToList();
        selected = options.FirstOrDefault() ?? "";
        Height = 38;
        Width = 210;
        Font = Visuals.Font(9.5f);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.ComboBox;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);

        menu = new ContextMenuStrip
        {
            BackColor = Visuals.SurfaceRaised,
            ForeColor = Visuals.Text,
            Renderer = new ToolStripProfessionalRenderer(new SelectMenuColors()),
            ShowImageMargin = false,
            AutoSize = false,
            Padding = new Padding(2),
        };
        foreach (var option in options)
        {
            var item = new ToolStripMenuItem(option)
            {
                AutoSize = false,
                Height = 36,
                Font = Visuals.Font(9),
                CheckOnClick = false,
            };
            item.Click += (_, _) => Text = option;
            menu.Items.Add(item);
        }
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnter(EventArgs e) { Invalidate(); base.OnEnter(e); }
    protected override void OnLeave(EventArgs e) { Invalidate(); base.OnLeave(e); }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Focus();
        var itemHeight = this.Dp(36);
        menu.Width = Width;
        menu.Height = options.Count * itemHeight + this.Dp(4);
        foreach (ToolStripMenuItem item in menu.Items)
        {
            item.Width = Width - 4;
            item.Height = itemHeight;
            item.Checked = string.Equals(item.Text, selected, StringComparison.Ordinal);
        }
        menu.Show(this, new Point(0, Height + 4));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) menu.Dispose();
        base.Dispose(disposing);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down or Keys.Space or Keys.Enter || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Arrow keys cycle in place; Space and Enter open the list.
        var index = options.IndexOf(selected);
        if (e.KeyCode is Keys.Down or Keys.Up && index >= 0 && options.Count > 0)
        {
            var step = e.KeyCode == Keys.Down ? 1 : -1;
            Text = options[(index + step + options.Count) % options.Count];
            e.Handled = true;
        }
        else if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            OnClick(EventArgs.Empty);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(.5f, .5f, Width - 1.5f, Height - 1.5f);
        var focused = Focused && ShowFocusCues;
        using var fill = new SolidBrush(hovered ? Visuals.SurfaceHover : Visuals.SurfaceRaised);
        using var border = new Pen(focused ? Visuals.Accent : hovered ? Visuals.Muted : Visuals.Border);
        e.Graphics.FillRoundedRectangle(fill, bounds, 9);
        e.Graphics.DrawRoundedRectangle(border, bounds, 9);

        var padding = this.Dp(13);
        var chevron = this.Dp(20);
        TextRenderer.DrawText(e.Graphics, selected, Font, new Rectangle(padding, 0, Width - padding - chevron - this.Dp(10), Height), Visuals.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        var arm = this.Dp(5);
        var centerX = Width - chevron;
        var centerY = Height / 2f;
        using var arrow = new Pen(Visuals.TextSecondary, Math.Max(1.5f, 1.5f * this.Scale())) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        e.Graphics.DrawLines(arrow, new PointF[] { new(centerX - arm, centerY - arm / 2f), new(centerX, centerY + arm / 2f), new(centerX + arm, centerY - arm / 2f) });
    }

    private sealed class SelectMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Visuals.SurfaceRaised;
        public override Color MenuItemSelected => Visuals.SurfaceHover;
        public override Color MenuItemBorder => Visuals.Border;
        public override Color ImageMarginGradientBegin => Visuals.SurfaceRaised;
        public override Color ImageMarginGradientMiddle => Visuals.SurfaceRaised;
        public override Color ImageMarginGradientEnd => Visuals.SurfaceRaised;
    }
}

public sealed class ToggleRow : RoundedPanel
{
    private readonly Label title;
    private readonly Label description;
    private readonly ToggleSwitch toggle = new();

    public bool Checked { get => toggle.Checked; set => toggle.Checked = value; }
    public event EventHandler? CheckedChanged { add => toggle.CheckedChanged += value; remove => toggle.CheckedChanged -= value; }

    public ToggleRow(string titleText, string descriptionText)
    {
        Height = 72;
        Radius = 12;
        BackColor = Visuals.Surface;
        Cursor = Cursors.Hand;
        title = Visuals.Label(titleText, 10, false, FontStyle.Bold);
        description = Visuals.Label(descriptionText, 8.5f, true);
        toggle.AccessibleName = titleText;
        toggle.AccessibleDescription = descriptionText;
        toggle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        Controls.AddRange([title, description, toggle]);

        Resize += (_, _) => LayoutChildren();
        foreach (Control target in new Control[] { this, title, description })
        {
            target.Click += (_, _) => { toggle.Checked = !toggle.Checked; toggle.Focus(); };
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        LayoutChildren();
    }

    private void LayoutChildren()
    {
        var padding = this.Dp(16);
        title.Location = new Point(padding, this.Dp(14));
        description.Location = new Point(padding, this.Dp(39));
        description.MaximumSize = new Size(Math.Max(this.Dp(160), Width - toggle.Width - padding * 3), 0);
        toggle.Location = new Point(Width - toggle.Width - padding, (Height - toggle.Height) / 2);
    }
}

/// <summary>A rounded status chip that sizes itself to its text.</summary>
public sealed class StatusPill : Control
{
    private Color dotColor = Visuals.Success;

    public Color DotColor
    {
        get => dotColor;
        set { dotColor = value; Invalidate(); }
    }

    public Color FillColor { get; set; } = Visuals.SuccessSurface;

    public StatusPill()
    {
        Height = 28;
        Width = 138;
        Font = Visuals.Font(8.5f, FontStyle.Bold);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        MeasureAndResize();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        MeasureAndResize();
    }

    /// <summary>Measures the label instead of relying on hard-coded pixel widths.</summary>
    private void MeasureAndResize()
    {
        var measured = TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, Height), TextFormatFlags.NoPrefix);
        var target = this.Dp(24) + measured.Width + this.Dp(14);
        if (Width == target) return;
        var right = Right;
        Width = target;
        if (Anchor.HasFlag(AnchorStyles.Right) && Parent is not null) Left = right - target;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var fill = new SolidBrush(FillColor);
        e.Graphics.FillRoundedRectangle(fill, new RectangleF(0, 0, Width - 1, Height - 1), Height / 2f);
        var dot = this.Dp(6);
        using var dotBrush = new SolidBrush(DotColor);
        e.Graphics.FillEllipse(dotBrush, this.Dp(11), (Height - dot) / 2f, dot, dot);
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(this.Dp(24), 0, Width - this.Dp(30), Height), DotColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Minimize / maximize / close glyph button for the custom title bar.</summary>
public sealed class CaptionButton : Control
{
    public enum Glyph { Minimize, Maximize, Restore, Close }

    private bool hovered;
    private Glyph glyph;

    public Glyph Kind
    {
        get => glyph;
        set { glyph = value; Invalidate(); }
    }

    public CaptionButton(Glyph kind)
    {
        glyph = kind;
        Size = new Size(46, 38);
        TabStop = false;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = kind.ToString();
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Visuals.Canvas);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (hovered)
        {
            using var hover = new SolidBrush(glyph == Glyph.Close ? Color.FromArgb(196, 43, 40) : Visuals.SurfaceHover);
            e.Graphics.FillRectangle(hover, ClientRectangle);
        }

        var color = hovered && glyph == Glyph.Close ? Color.White : Visuals.TextSecondary;
        using var pen = new Pen(color, Math.Max(1f, this.Scale()));
        var size = this.Dp(10);
        var left = (Width - size) / 2f;
        var top = (Height - size) / 2f;

        switch (glyph)
        {
            case Glyph.Minimize:
                e.Graphics.DrawLine(pen, left, top + size / 2f, left + size, top + size / 2f);
                break;
            case Glyph.Maximize:
                e.Graphics.DrawRectangle(pen, left, top, size, size);
                break;
            case Glyph.Restore:
                e.Graphics.DrawRectangle(pen, left, top + this.Dp(3), size - this.Dp(3), size - this.Dp(3));
                e.Graphics.DrawLines(pen, new PointF[] { new(left + this.Dp(3), top + this.Dp(3)), new(left + this.Dp(3), top), new(left + size, top), new(left + size, top + size - this.Dp(3)), new(left + size - this.Dp(3), top + size - this.Dp(3)) });
                break;
            case Glyph.Close:
                e.Graphics.DrawLine(pen, left, top, left + size, top + size);
                e.Graphics.DrawLine(pen, left + size, top, left, top + size);
                break;
        }
    }
}

/// <summary>
/// Base window with custom dark chrome.
///
/// The frame keeps the native window styles (<c>WS_SIZEBOX</c>,
/// <c>WS_MAXIMIZEBOX</c>) and reports the title bar as <c>HTCAPTION</c>, so
/// dragging, Aero Snap, Win+Arrow, double-click-to-maximize and edge resizing
/// all behave like a normal Windows window — none of which worked while the
/// title bar was dragged by hand with <c>WM_NCLBUTTONDOWN</c>.
/// </summary>
public class ModernForm : Form
{
    private const int WM_NCCALCSIZE = 0x0083;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int HTCLIENT = 1, HTCAPTION = 2;
    private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private const int WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000, WS_SIZEBOX = 0x00040000;

    private readonly bool resizable;
    private readonly CaptionButton? maximize;

    protected readonly Panel ContentHost = new() { Dock = DockStyle.Fill, BackColor = Visuals.Background };
    protected readonly Panel TitleBar = new() { Dock = DockStyle.Top, Height = 48, BackColor = Visuals.Canvas };

    /// <summary>When true, Escape closes the window. Enabled for dialogs.</summary>
    protected bool CloseOnEscape { get; set; }

    protected ModernForm(string title, Size size, bool resizable = false)
    {
        this.resizable = resizable;
        Text = title;
        Icon = Visuals.AppIcon;
        ClientSize = size;
        BackColor = Visuals.Border;
        ForeColor = Visuals.Text;
        Font = Visuals.Font(9f);
        FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(1);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = size;
        KeyPreview = true;
        DoubleBuffered = true;

        var mark = new BrandMark { Location = new Point(14, 12), Size = new Size(24, 24) };
        var windowTitle = Visuals.Label(title, 9, false, FontStyle.Bold);
        windowTitle.Location = new Point(48, 15);

        var close = new CaptionButton(CaptionButton.Glyph.Close) { Anchor = AnchorStyles.Top | AnchorStyles.Right };
        close.Click += (_, _) => Close();
        var minimize = new CaptionButton(CaptionButton.Glyph.Minimize) { Anchor = AnchorStyles.Top | AnchorStyles.Right };
        minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;

        var buttons = new List<Control> { close, minimize };
        if (resizable)
        {
            maximize = new CaptionButton(CaptionButton.Glyph.Maximize) { Anchor = AnchorStyles.Top | AnchorStyles.Right };
            maximize.Click += (_, _) => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
            buttons.Add(maximize);
        }

        TitleBar.Controls.Add(mark);
        TitleBar.Controls.Add(windowTitle);
        foreach (var button in buttons) TitleBar.Controls.Add(button);
        TitleBar.Resize += (_, _) => LayoutCaption(buttons);

        Controls.Add(ContentHost);
        Controls.Add(TitleBar);
        Shown += (_, _) => Visuals.ApplyWindowStyle(this);
    }

    private void LayoutCaption(List<Control> buttons)
    {
        var right = TitleBar.Width - 2;
        foreach (var button in buttons)
        {
            right -= button.Width;
            button.Location = new Point(right, 5);
        }
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        if (maximize is not null) maximize.Kind = WindowState == FormWindowState.Maximized ? CaptionButton.Glyph.Restore : CaptionButton.Glyph.Maximize;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (CloseOnEscape && e.KeyCode == Keys.Escape) { Close(); e.Handled = true; return; }
        base.OnKeyDown(e);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.Style |= WS_MINIMIZEBOX;
            if (resizable) parameters.Style |= WS_SIZEBOX | WS_MAXIMIZEBOX;
            return parameters;
        }
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_NCCALCSIZE when m.WParam != IntPtr.Zero:
                // Claim the whole window as client area so WS_SIZEBOX does not draw a frame.
                m.Result = IntPtr.Zero;
                return;
            case WM_NCHITTEST:
                m.Result = HitTest(m.LParam);
                return;
            case WM_GETMINMAXINFO:
                base.WndProc(ref m);
                ConstrainMaximizedBounds(m.LParam);
                return;
        }
        base.WndProc(ref m);
    }

    private IntPtr HitTest(IntPtr lParam)
    {
        var packed = (int)(long)lParam;
        var screenPoint = new Point(unchecked((short)(packed & 0xFFFF)), unchecked((short)((packed >> 16) & 0xFFFF)));
        var point = PointToClient(screenPoint);

        if (resizable && WindowState == FormWindowState.Normal)
        {
            var grip = this.Dp(6);
            var left = point.X <= grip;
            var right = point.X >= ClientSize.Width - grip;
            var top = point.Y <= grip;
            var bottom = point.Y >= ClientSize.Height - grip;
            if (top && left) return HTTOPLEFT;
            if (top && right) return HTTOPRIGHT;
            if (bottom && left) return HTBOTTOMLEFT;
            if (bottom && right) return HTBOTTOMRIGHT;
            if (left) return HTLEFT;
            if (right) return HTRIGHT;
            if (top) return HTTOP;
            if (bottom) return HTBOTTOM;
        }

        if (!TitleBar.Bounds.Contains(point)) return HTCLIENT;
        // Leave the caption buttons clickable; everything else drags the window.
        return TitleBar.GetChildAtPoint(TitleBar.PointToClient(screenPoint)) is CaptionButton ? HTCLIENT : HTCAPTION;
    }

    /// <summary>Keeps a maximized borderless window inside the work area instead of covering the taskbar.</summary>
    private void ConstrainMaximizedBounds(IntPtr lParam)
    {
        var screen = Screen.FromHandle(Handle);
        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        info.MaxPosition = new NativePoint(Math.Abs(screen.WorkingArea.Left - screen.Bounds.Left), Math.Abs(screen.WorkingArea.Top - screen.Bounds.Top));
        info.MaxSize = new NativePoint(screen.WorkingArea.Width, screen.WorkingArea.Height);
        info.MinTrackSize = new NativePoint(MinimumSize.Width, MinimumSize.Height);
        Marshal.StructureToPtr(info, lParam, false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }
}

public sealed class BrandMark : Control
{
    public BrandMark() => SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Visuals.Canvas);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = Width / 24f;
        using var border = new Pen(Visuals.Text, 1.4f * scale);
        e.Graphics.DrawRoundedRectangle(border, new RectangleF(scale, scale, Width - 3 * scale, Height - 3 * scale), 6 * scale);
        using var glyph = new Pen(Visuals.Text, 1.8f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        e.Graphics.DrawLines(glyph, new PointF[] { new(7 * scale, 8 * scale), new(11 * scale, 12 * scale), new(7 * scale, 16 * scale) });
        e.Graphics.DrawLine(glyph, 13 * scale, 16 * scale, 18 * scale, 16 * scale);
        using var live = new SolidBrush(Visuals.Success);
        e.Graphics.FillEllipse(live, Width - 7 * scale, 4 * scale, 4 * scale, 4 * scale);
    }
}
