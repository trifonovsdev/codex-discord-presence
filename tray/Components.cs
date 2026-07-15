using System.Drawing.Drawing2D;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace CodexPresence;

public enum ButtonKind { Primary, Secondary, Ghost, Danger }

public sealed class ModernButton : Button
{
    private bool hovered;
    private bool pressed;
    public ButtonKind Kind { get; set; } = ButtonKind.Secondary;
    public string? IconGlyph { get; set; }
    public int Radius { get; set; } = 10;

    public ModernButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = Visuals.Font(9.5f, FontStyle.Bold);
        Cursor = Cursors.Hand;
        TabStop = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Visuals.Background);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        var (background, foreground, border) = Colors();
        if (!Enabled) { background = Visuals.Surface; foreground = Visuals.Muted; }
        else if (pressed) background = ControlPaint.Dark(background, .08f);
        else if (hovered) background = Kind == ButtonKind.Primary ? Color.White : Visuals.SurfaceHover;

        using var fill = new SolidBrush(background);
        e.Graphics.FillRoundedRectangle(fill, bounds, Radius);
        if (border != Color.Transparent)
        {
            using var pen = new Pen(border);
            e.Graphics.DrawRoundedRectangle(pen, bounds, Radius);
        }

        var text = string.IsNullOrWhiteSpace(IconGlyph) ? Text : $"{IconGlyph}   {Text}";
        TextRenderer.DrawText(e.Graphics, text, Font, Rectangle.Round(bounds), foreground,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(Rectangle.Round(bounds), -4, -4), foreground, background);
    }

    private (Color Background, Color Foreground, Color Border) Colors() => Kind switch
    {
        ButtonKind.Primary => (Visuals.Accent, Visuals.AccentText, Color.Transparent),
        ButtonKind.Ghost => (Color.Transparent, Visuals.TextSecondary, Color.Transparent),
        ButtonKind.Danger => (Visuals.DangerSurface, Visuals.Danger, Color.Transparent),
        _ => (Visuals.SurfaceRaised, Visuals.Text, Visuals.Border),
    };
}

public class RoundedPanel : Panel
{
    public int Radius { get; set; } = 14;
    public Color BorderColor { get; set; } = Visuals.BorderSoft;
    public int BorderWidth { get; set; } = 1;

    public RoundedPanel()
    {
        BackColor = Visuals.Surface;
        Margin = Padding.Empty;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        var previous = Region;
        if (Width > 0 && Height > 0)
        {
            using var path = Visuals.RoundedPath(new RectangleF(0, 0, Width, Height), Radius);
            Region = new Region(path);
        }
        previous?.Dispose();
        Invalidate();
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
    private bool isChecked;
    private bool hovered;
    public bool Checked
    {
        get => isChecked;
        set { if (isChecked == value) return; isChecked = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); }
    }
    public event EventHandler? CheckedChanged;

    public ToggleSwitch()
    {
        Size = new Size(42, 24);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.CheckButton;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
    }

    protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode is Keys.Space or Keys.Enter) { Checked = !Checked; e.Handled = true; } base.OnKeyDown(e); }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var track = Checked ? (hovered ? Color.FromArgb(84, 230, 177) : Visuals.Success) : (hovered ? Visuals.SurfaceHover : Visuals.Border);
        using var trackBrush = new SolidBrush(track);
        e.Graphics.FillRoundedRectangle(trackBrush, new RectangleF(0, 1, Width, Height - 2), Height / 2f);
        var diameter = Height - 8;
        var x = Checked ? Width - diameter - 4 : 4;
        using var thumb = new SolidBrush(Checked ? Color.FromArgb(10, 35, 28) : Visuals.TextSecondary);
        e.Graphics.FillEllipse(thumb, x, 4, diameter, diameter);
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
    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        menu.Width = Width;
        menu.Height = options.Count * 36 + 4;
        foreach (ToolStripMenuItem item in menu.Items)
        {
            item.Width = Width - 4;
            item.Checked = string.Equals(item.Text, selected, StringComparison.Ordinal);
        }
        menu.Show(this, new Point(0, Height + 4));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) menu.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter or Keys.Down) { OnClick(EventArgs.Empty); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(.5f, .5f, Width - 1.5f, Height - 1.5f);
        using var fill = new SolidBrush(hovered ? Visuals.SurfaceHover : Visuals.SurfaceRaised);
        using var border = new Pen(hovered ? Visuals.Muted : Visuals.Border);
        e.Graphics.FillRoundedRectangle(fill, bounds, 9);
        e.Graphics.DrawRoundedRectangle(border, bounds, 9);
        TextRenderer.DrawText(e.Graphics, selected, Font, new Rectangle(13, 0, Width - 42, Height), Visuals.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        using var arrow = new Pen(Visuals.TextSecondary, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        e.Graphics.DrawLines(arrow, new PointF[] { new(Width - 24, 16), new(Width - 19, 21), new(Width - 14, 16) });
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
        title = Visuals.Label(titleText, 10, false, FontStyle.Bold);
        description = Visuals.Label(descriptionText, 8.5f, true);
        title.Location = new Point(16, 14);
        description.Location = new Point(16, 39);
        toggle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        toggle.AccessibleName = titleText;
        Controls.AddRange([title, description, toggle]);
        Resize += (_, _) => toggle.Location = new Point(Width - toggle.Width - 16, 24);
        Click += (_, _) => toggle.Checked = !toggle.Checked;
        title.Click += (_, _) => toggle.Checked = !toggle.Checked;
        description.Click += (_, _) => toggle.Checked = !toggle.Checked;
    }
}

public sealed class StatusPill : Control
{
    public Color DotColor { get; set; } = Visuals.Success;
    public Color FillColor { get; set; } = Visuals.SuccessSurface;
    public StatusPill()
    {
        Height = 28;
        Width = 138;
        Font = Visuals.Font(8.5f, FontStyle.Bold);
        TextChanged += (_, _) => Invalidate();
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var fill = new SolidBrush(FillColor);
        e.Graphics.FillRoundedRectangle(fill, new RectangleF(0, 0, Width - 1, Height - 1), Height / 2f);
        using var dot = new SolidBrush(DotColor);
        e.Graphics.FillEllipse(dot, 11, 11, 6, 6);
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(24, 0, Width - 30, Height), DotColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

public class ModernForm : Form
{
    protected readonly Panel ContentHost = new() { Dock = DockStyle.Fill, BackColor = Visuals.Background };
    protected readonly Panel TitleBar = new() { Dock = DockStyle.Top, Height = 48, BackColor = Visuals.Canvas };
    private readonly Label windowTitle;

    protected ModernForm(string title, Size size)
    {
        Text = title;
        Icon = Visuals.CreateIcon();
        ClientSize = size;
        BackColor = Visuals.Border;
        ForeColor = Visuals.Text;
        Font = Visuals.Font(9f);
        FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(1);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = size;

        var mark = new BrandMark { Location = new Point(14, 12), Size = new Size(24, 24) };
        windowTitle = Visuals.Label(title, 9, false, FontStyle.Bold);
        windowTitle.Location = new Point(48, 15);
        var minimize = Visuals.Button("—", ButtonKind.Ghost);
        minimize.Size = new Size(44, 38);
        minimize.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        var close = Visuals.Button("×", ButtonKind.Ghost);
        close.Size = new Size(44, 38);
        close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        close.Font = Visuals.Font(14);
        close.Click += (_, _) => Close();
        TitleBar.Controls.AddRange([mark, windowTitle, minimize, close]);
        TitleBar.Resize += (_, _) => { close.Location = new Point(TitleBar.Width - 48, 5); minimize.Location = new Point(close.Left - 44, 5); };
        TitleBar.MouseDown += DragWindow;
        windowTitle.MouseDown += DragWindow;

        Controls.Add(ContentHost);
        Controls.Add(TitleBar);
        Shown += (_, _) => Visuals.ApplyWindowStyle(this);
    }

    private void DragWindow(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, 0xA1, 0x2, 0);
    }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, int wParam, int lParam);
}

public sealed class BrandMark : Control
{
    public BrandMark() => SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var border = new Pen(Visuals.Text, 1.4f);
        e.Graphics.DrawRoundedRectangle(border, new RectangleF(1, 1, Width - 3, Height - 3), 6);
        using var glyph = new Pen(Visuals.Text, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        e.Graphics.DrawLines(glyph, new PointF[] { new(7, 8), new(11, 12), new(7, 16) });
        e.Graphics.DrawLine(glyph, 13, 16, 18, 16);
        using var live = new SolidBrush(Visuals.Success);
        e.Graphics.FillEllipse(live, Width - 7, 4, 4, 4);
    }
}
