using System.Drawing.Drawing2D;
using System.Diagnostics.CodeAnalysis;

namespace CodexPresence;

public enum ButtonKind { Primary, Secondary, Ghost, Danger }

public sealed class ModernButton : Button
{
    private float hoverProgress;
    private float pressProgress;
    private IDisposable? hoverMotion;
    private IDisposable? pressMotion;
    private ButtonKind kind = ButtonKind.Secondary;
    private UiIcon? icon;
    private bool isSelected;

    public ButtonKind Kind
    {
        get => kind;
        set { if (kind == value) return; kind = value; Invalidate(); }
    }

    public UiIcon? Icon
    {
        get => icon;
        set { if (icon == value) return; icon = value; Invalidate(); }
    }

    public int Radius { get; set; } = 5;

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value) return;
            isSelected = value;
            if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
        }
    }

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

    protected override void OnMouseEnter(EventArgs e) { AnimateHover(1f); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { AnimateHover(0f); AnimatePress(0f); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { AnimatePress(1f); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { AnimatePress(0f); base.OnMouseUp(e); }
    protected override void OnEnter(EventArgs e) { Invalidate(); base.OnEnter(e); }
    protected override void OnLeave(EventArgs e) { Invalidate(); base.OnLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Visuals.Background);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var pressInset = this.Dp(1) * pressProgress;
        var offsetY = (int)Math.Round(this.Dp(.5f) * pressProgress);
        var bounds = new RectangleF(
            0.5f + pressInset,
            0.5f + pressInset,
            Width - 1 - pressInset * 2,
            Height - 1 - pressInset * 2);
        var radius = this.Dp(Radius);
        var (background, foreground, border) = Colors();
        if (!Enabled) { background = Visuals.Surface; foreground = Visuals.Muted; border = Visuals.BorderSoft; }
        else
        {
            var hover = Kind == ButtonKind.Primary ? Color.White : Visuals.SurfaceHover;
            background = Visuals.Blend(background, hover, hoverProgress);
            if (pressProgress > 0) background = Visuals.Blend(background, ControlPaint.Dark(background, .08f), pressProgress);
        }

        using var fill = new SolidBrush(background);
        e.Graphics.FillRoundedRectangle(fill, bounds, radius);
        if (border != Color.Transparent)
        {
            using var pen = new Pen(border);
            e.Graphics.DrawRoundedRectangle(pen, bounds, radius);
        }

        DrawContent(e.Graphics, foreground, offsetY);

        // A visible focus ring is the only way to drive this UI from the keyboard.
        if (Focused && ShowFocusCues)
        {
            var inset = this.Dp(3);
            using var focus = new Pen(Visuals.FocusRing, Math.Max(1f, this.Scale()));
            e.Graphics.DrawRoundedRectangle(focus, RectangleF.Inflate(bounds, -inset, -inset), Math.Max(2, radius - inset));
        }
    }

    private (Color Background, Color Foreground, Color Border) Colors() => Kind switch
    {
        ButtonKind.Primary => (Visuals.Accent, Visuals.AccentText, Color.Transparent),
        ButtonKind.Ghost => (Color.Transparent, Visuals.TextSecondary, Color.Transparent),
        ButtonKind.Danger => (Visuals.DangerSurface, Visuals.Danger, Color.Transparent),
        _ => (Visuals.SurfaceRaised, Visuals.Text, Visuals.Border),
    };

    private void DrawContent(Graphics graphics, Color color, int offsetY)
    {
        var padding = this.Dp(12);
        var iconSize = this.Dp(17);
        var gap = this.Dp(8);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

        if (Icon is null)
        {
            flags |= TextAlign == ContentAlignment.MiddleLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter;
            TextRenderer.DrawText(graphics, Text, Font, new Rectangle(padding, offsetY, Width - padding * 2, Height), color, flags);
            return;
        }

        if (string.IsNullOrWhiteSpace(Text))
        {
            UiIcons.Draw(graphics, Icon.Value, new RectangleF((Width - iconSize) / 2f, (Height - iconSize) / 2f + offsetY, iconSize, iconSize), color);
            return;
        }

        var measured = TextRenderer.MeasureText(graphics, Text, Font, new Size(Width, Height), TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        var contentWidth = iconSize + gap + measured.Width;
        var startX = TextAlign == ContentAlignment.MiddleLeft ? padding : Math.Max(padding, (Width - contentWidth) / 2);
        UiIcons.Draw(graphics, Icon.Value, new RectangleF(startX, (Height - iconSize) / 2f + offsetY, iconSize, iconSize), color);
        TextRenderer.DrawText(graphics, Text, Font, new Rectangle(startX + iconSize + gap, offsetY, Width - startX - iconSize - gap - padding, Height), color,
            flags | TextFormatFlags.Left);
    }

    private void AnimateHover(float target)
    {
        hoverMotion?.Dispose();
        var start = hoverProgress;
        hoverMotion = MotionClock.Animate(this, 120, value =>
        {
            hoverProgress = MotionClock.Lerp(start, target, value);
            Invalidate();
        });
    }

    private void AnimatePress(float target)
    {
        pressMotion?.Dispose();
        var start = pressProgress;
        pressMotion = MotionClock.Animate(this, 80, value =>
        {
            pressProgress = MotionClock.Lerp(start, target, value);
            Invalidate();
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            hoverMotion?.Dispose();
            pressMotion?.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new ModernButtonAccessibleObject(this);

    private sealed class ModernButtonAccessibleObject(ModernButton owner) : ControlAccessibleObject(owner)
    {
        public override string? DefaultAction => "Press";
        public override AccessibleRole Role => owner.AccessibleRole == AccessibleRole.Default ? AccessibleRole.PushButton : owner.AccessibleRole;
        public override AccessibleStates State => base.State | (owner.IsSelected ? AccessibleStates.Selected : AccessibleStates.None);
        public override int GetChildCount() => 0;
        public override void DoDefaultAction() => owner.PerformClick();
    }
}

/// <summary>
/// A rounded surface. The corners are painted rather than clipped with a
/// <see cref="Region"/>: regions are not antialiased, which is what made every
/// card in the previous build show visibly stair-stepped corners.
/// </summary>
public class RoundedPanel : Panel
{
    public int Radius { get; set; } = 8;
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
        e.Graphics.FillRoundedRectangle(brush, new RectangleF(0, 0, Width, Height), this.Dp(Radius));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (BorderWidth <= 0) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(BorderColor, Math.Max(1f, BorderWidth * this.Scale()));
        e.Graphics.DrawRoundedRectangle(pen, new RectangleF(.5f, .5f, Width - 1.5f, Height - 1.5f), this.Dp(Radius));
    }
}

public sealed class ToggleSwitch : Control
{
    private float progress;
    private float hoverProgress;
    private bool isChecked;
    private IDisposable? toggleMotion;
    private IDisposable? hoverMotion;

    public bool Checked
    {
        get => isChecked;
        set => SetChecked(value, animate: true);
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

    public override string ToString() => $"{Name}: {(isChecked ? "on" : "off")}";
    protected override AccessibleObject CreateAccessibilityInstance() => new ToggleAccessibleObject(this);

    protected override void OnClick(EventArgs e) { Checked = !Checked; Focus(); base.OnClick(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) { SetChecked(!Checked, animate: false); e.Handled = true; }
        base.OnKeyDown(e);
    }
    protected override void OnMouseEnter(EventArgs e) { AnimateHover(1f); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { AnimateHover(0f); base.OnMouseLeave(e); }
    protected override void OnEnter(EventArgs e) { Invalidate(); base.OnEnter(e); }
    protected override void OnLeave(EventArgs e) { Invalidate(); base.OnLeave(e); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Space or Keys.Enter || base.IsInputKey(keyData);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        progress = isChecked ? 1f : 0f;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var off = Visuals.Blend(Visuals.Border, Visuals.SurfaceHover, hoverProgress);
        var on = Visuals.Blend(Visuals.Success, Color.FromArgb(111, 231, 181), hoverProgress);
        using var trackBrush = new SolidBrush(Visuals.Blend(off, on, progress));
        e.Graphics.FillRoundedRectangle(trackBrush, new RectangleF(0, 1, Width, Height - 2), Height / 2f);

        var margin = this.Dp(4);
        var diameter = Height - margin * 2;
        var travel = Width - diameter - margin * 2;
        using var thumb = new SolidBrush(Visuals.Blend(Visuals.TextSecondary, Color.FromArgb(10, 35, 28), progress));
        e.Graphics.FillEllipse(thumb, margin + travel * progress, margin, diameter, diameter);

        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(Visuals.FocusRing, Math.Max(1f, this.Scale()));
            e.Graphics.DrawRoundedRectangle(focus, new RectangleF(-1.5f, -0.5f, Width + 2, Height - 1), Height / 2f);
        }
    }

    private void AnimateToggle()
    {
        toggleMotion?.Dispose();
        var start = progress;
        var target = isChecked ? 1f : 0f;
        toggleMotion = MotionClock.Animate(this, 160, value =>
        {
            progress = Math.Clamp(MotionClock.Lerp(start, target, value), 0f, 1f);
            Invalidate();
        }, MotionEasing.EaseOutCubic);
    }

    private void SetChecked(bool value, bool animate)
    {
        if (isChecked == value) return;
        isChecked = value;
        if (animate) AnimateToggle();
        else
        {
            toggleMotion?.Dispose();
            progress = isChecked ? 1f : 0f;
            Invalidate();
        }
        if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
        CheckedChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AnimateHover(float target)
    {
        hoverMotion?.Dispose();
        var start = hoverProgress;
        hoverMotion = MotionClock.Animate(this, 120, value =>
        {
            hoverProgress = MotionClock.Lerp(start, target, value);
            Invalidate();
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            toggleMotion?.Dispose();
            hoverMotion?.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class ToggleAccessibleObject(ToggleSwitch owner) : ControlAccessibleObject(owner)
    {
        public override string? DefaultAction => owner.Checked ? "Turn off" : "Turn on";
        public override AccessibleStates State => owner.Checked
            ? base.State | AccessibleStates.Checked
            : base.State;

        public override void DoDefaultAction() => owner.SetChecked(!owner.Checked, animate: false);
    }
}

public sealed class ModernSelect : Control
{
    private readonly List<string> options;
    private readonly ContextMenuStrip menu;
    private string selected = "";
    private float hoverProgress;
    private IDisposable? hoverMotion;

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
            if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
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

    protected override void OnMouseEnter(EventArgs e) { AnimateHover(1f); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { AnimateHover(0f); base.OnMouseLeave(e); }
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
        if (disposing)
        {
            hoverMotion?.Dispose();
            menu.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down or Keys.Space or Keys.Enter || base.IsInputKey(keyData);
    protected override AccessibleObject CreateAccessibilityInstance() => new SelectAccessibleObject(this);

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
        using var fill = new SolidBrush(Visuals.Blend(Visuals.SurfaceRaised, Visuals.SurfaceHover, hoverProgress));
        using var border = new Pen(focused ? Visuals.FocusRing : Visuals.Blend(Visuals.Border, Visuals.Muted, hoverProgress));
        var radius = this.Dp(5);
        e.Graphics.FillRoundedRectangle(fill, bounds, radius);
        e.Graphics.DrawRoundedRectangle(border, bounds, radius);

        var padding = this.Dp(13);
        var chevron = this.Dp(20);
        TextRenderer.DrawText(e.Graphics, selected, Font, new Rectangle(padding, 0, Width - padding - chevron - this.Dp(10), Height), Visuals.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        var iconSize = this.Dp(14);
        UiIcons.Draw(e.Graphics, UiIcon.ChevronDown, new RectangleF(Width - chevron - iconSize / 2f, (Height - iconSize) / 2f, iconSize, iconSize), Visuals.TextSecondary);
    }

    private void AnimateHover(float target)
    {
        hoverMotion?.Dispose();
        var start = hoverProgress;
        hoverMotion = MotionClock.Animate(this, 120, value =>
        {
            hoverProgress = MotionClock.Lerp(start, target, value);
            Invalidate();
        });
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

    private sealed class SelectAccessibleObject(ModernSelect owner) : ControlAccessibleObject(owner)
    {
        public override string? Value => owner.Text;
        public override string? DefaultAction => "Open list";
        public override AccessibleStates State => base.State | (owner.menu.Visible ? AccessibleStates.Expanded : AccessibleStates.Collapsed);
        public override void DoDefaultAction() => owner.OnClick(EventArgs.Empty);
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
        Height = 64;
        Radius = 0;
        BorderWidth = 0;
        BackColor = Visuals.Background;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.Grouping;
        AccessibleName = titleText;
        title = Visuals.Label(titleText, 10, false, FontStyle.Bold);
        description = Visuals.Label(descriptionText, 8.5f, true);
        title.AutoSize = false;
        title.AutoEllipsis = true;
        description.AutoSize = false;
        description.AutoEllipsis = true;
        toggle.AccessibleName = titleText;
        toggle.AccessibleDescription = descriptionText;
        toggle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        Controls.AddRange([title, description, toggle]);

        Resize += (_, _) => LayoutChildren();
        foreach (Control target in new Control[] { this, title, description })
        {
            target.Click += (_, _) => { toggle.Checked = !toggle.Checked; toggle.Focus(); };
        }
        Paint += (_, e) =>
        {
            using var rule = new Pen(Visuals.BorderSoft);
            e.Graphics.DrawLine(rule, 0, Height - 1, Width, Height - 1);
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        LayoutChildren();
    }

    private void LayoutChildren()
    {
        var padding = this.Dp(16);
        var textWidth = Math.Max(this.Dp(120), Width - toggle.Width - padding * 3);
        title.SetBounds(padding, this.Dp(10), textWidth, this.Dp(22));
        description.SetBounds(padding, this.Dp(34), textWidth, this.Dp(20));
        toggle.Location = new Point(Width - toggle.Width - padding, (Height - toggle.Height) / 2);
    }
}

/// <summary>A rounded status chip that sizes itself to its text.</summary>
public sealed class StatusPill : Control
{
    private Color dotColor = Visuals.Success;
    private Color fillColor = Visuals.SuccessSurface;
    private bool isLive;
    private float pulse;
    private IDisposable? pulseMotion;
    private DateTimeOffset lastConfirmationAt;

    public Color DotColor
    {
        get => dotColor;
        set { dotColor = value; Invalidate(); }
    }

    public Color FillColor
    {
        get => fillColor;
        set { if (fillColor == value) return; fillColor = value; Invalidate(); }
    }

    public bool IsLive
    {
        get => isLive;
        set
        {
            if (isLive == value) return;
            isLive = value;
            ConfirmLiveState();
            Invalidate();
        }
    }

    public StatusPill()
    {
        Height = 28;
        Width = 138;
        Font = Visuals.Font(8.5f, FontStyle.Bold);
        AccessibleRole = AccessibleRole.StatusBar;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        MeasureAndResize();
        if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        MeasureAndResize();
        pulse = 1f;
        Invalidate();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) pulseMotion?.Dispose();
        pulse = 1f;
        Invalidate();
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
        if (IsLive && !MotionClock.IsReduced)
        {
            var spread = this.Dp(8) * pulse;
            using var halo = new SolidBrush(Color.FromArgb((int)(48 * (1f - pulse)), DotColor));
            e.Graphics.FillEllipse(halo, this.Dp(11) - spread / 2f, (Height - dot) / 2f - spread / 2f, dot + spread, dot + spread);
        }
        using var dotBrush = new SolidBrush(DotColor);
        e.Graphics.FillEllipse(dotBrush, this.Dp(11), (Height - dot) / 2f, dot, dot);
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(this.Dp(24), 0, Width - this.Dp(30), Height), Visuals.TextSecondary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private void ConfirmLiveState()
    {
        pulseMotion?.Dispose();
        pulse = 1;
        if (!IsLive || !IsHandleCreated) { Invalidate(); return; }

        var now = DateTimeOffset.UtcNow;
        if (now - lastConfirmationAt < TimeSpan.FromSeconds(8)) { Invalidate(); return; }
        lastConfirmationAt = now;

        // A single acknowledgement communicates the state change. An endless
        // halo looked decorative and made a stable connection feel busy.
        pulse = 0;
        pulseMotion = MotionClock.Animate(this, 520, value =>
        {
            pulse = value;
            Invalidate();
        }, MotionEasing.EaseOutCubic);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) pulseMotion?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Base window that deliberately leaves chrome to Windows. Native captions
/// keep drag, Snap Layouts, resize handles, keyboard movement, and screen-reader
/// behavior reliable across Windows versions and DPI settings.
/// </summary>
public class ModernForm : Form
{
    protected readonly Panel ContentHost = new() { Dock = DockStyle.Fill, BackColor = Visuals.Background };

    /// <summary>When true, Escape closes the window. Enabled for dialogs.</summary>
    protected bool CloseOnEscape { get; set; }

    protected ModernForm(string title, Size size, bool resizable = false)
    {
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = title;
        Icon = Visuals.AppIcon;
        ClientSize = size;
        BackColor = Visuals.Background;
        ForeColor = Visuals.Text;
        Font = Visuals.Font(9f);
        FormBorderStyle = resizable ? FormBorderStyle.Sizable : FormBorderStyle.FixedDialog;
        MaximizeBox = resizable;
        MinimizeBox = resizable;
        Padding = Padding.Empty;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = SizeFromClientSize(size);
        KeyPreview = true;
        DoubleBuffered = true;
        SizeGripStyle = SizeGripStyle.Hide;

        Controls.Add(ContentHost);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Visuals.ApplyWindowStyle(this);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (WindowState != FormWindowState.Normal) return;

        var workingArea = Screen.FromControl(this).WorkingArea;
        var width = Math.Min(Width, workingArea.Width);
        var height = Math.Min(Height, workingArea.Height);
        if (MinimumSize.Width > width || MinimumSize.Height > height)
            MinimumSize = new Size(Math.Min(MinimumSize.Width, width), Math.Min(MinimumSize.Height, height));
        Size = new Size(width, height);
        Location = new Point(
            Math.Clamp(Left, workingArea.Left, workingArea.Right - width),
            Math.Clamp(Top, workingArea.Top, workingArea.Bottom - height));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (CloseOnEscape && e.KeyCode == Keys.Escape) { Close(); e.Handled = true; return; }
        base.OnKeyDown(e);
    }

}
