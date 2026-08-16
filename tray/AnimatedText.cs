using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace CodexPresence;

/// <summary>
/// A single-line status label that cross-fades text changes without moving
/// the control itself. The public <see cref="Text"/> value changes
/// synchronously, so accessibility clients never have to wait for motion.
/// </summary>
public sealed class AnimatedText : Control
{
    private const int TransitionDurationMs = 160;

    private Color textColor = Visuals.Text;
    private ContentAlignment textAlign = ContentAlignment.MiddleLeft;
    private int travelDp = 6;
    private string outgoingText = string.Empty;
    private string incomingText = string.Empty;
    private float outgoingOpacity;
    private float incomingOpacity = 1f;
    private float outgoingOffsetDp;
    private float incomingOffsetDp;
    private IDisposable? transitionMotion;
    private Bitmap? outgoingSnapshot;

    [Category("Appearance")]
    public Color TextColor
    {
        get => textColor;
        set
        {
            if (textColor == value) return;
            textColor = value;
            Invalidate();
        }
    }

    [Category("Appearance")]
    [DefaultValue(6)]
    public int TravelDp
    {
        get => travelDp;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), value, "Travel must be non-negative.");
            if (travelDp == value) return;
            travelDp = value;
            Invalidate();
        }
    }

    [Category("Appearance")]
    [DefaultValue(ContentAlignment.MiddleLeft)]
    public ContentAlignment TextAlign
    {
        get => textAlign;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new InvalidEnumArgumentException(nameof(value), (int)value, typeof(ContentAlignment));
            }
            if (textAlign == value) return;
            textAlign = value;
            Invalidate();
        }
    }

    [Browsable(true)]
    [EditorBrowsable(EditorBrowsableState.Always)]
    [AllowNull]
    public override string Text
    {
        get => base.Text;
        set
        {
            var nextText = value ?? string.Empty;
            if (string.Equals(base.Text, nextText, StringComparison.Ordinal)) return;

            var nextDisplayText = ToSingleLine(nextText);
            if (string.Equals(ToSingleLine(base.Text), nextDisplayText, StringComparison.Ordinal))
            {
                CancelTransition();
                base.Text = nextText;
                SettleVisual();
                return;
            }

            var interrupted = transitionMotion is not null;
            var previousSnapshot = interrupted ? CaptureCurrentVisual() : null;
            var incomingDominates = incomingOpacity >= outgoingOpacity;
            var previousText = !interrupted
                ? ToSingleLine(base.Text)
                : incomingDominates ? incomingText : outgoingText;
            var previousOffsetDp = !interrupted
                ? 0f
                : incomingDominates ? incomingOffsetDp : outgoingOffsetDp;

            CancelTransition();
            base.Text = nextText;
            StartTransition(previousText, previousOffsetDp, nextDisplayText, previousSnapshot);
        }
    }

    public AnimatedText()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.UserPaint,
            true);
        Size = new Size(160, 24);
        Font = Visuals.Font(9f);
        BackColor = Color.Transparent;
        TabStop = false;
        AccessibleRole = AccessibleRole.StaticText;
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
        Invalidate();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) SettleVisual();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        SettleVisual();
        base.OnHandleDestroyed(e);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        SettleVisual();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (transitionMotion is not null) SettleVisual();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (transitionMotion is not null) SettleVisual();
        else Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        if (transitionMotion is null)
        {
            DrawLayer(e.Graphics, ToSingleLine(base.Text), 1f, 0f);
            return;
        }

        if (outgoingSnapshot is not null) DrawSnapshot(e.Graphics, outgoingSnapshot, outgoingOpacity, outgoingOffsetDp);
        else DrawLayer(e.Graphics, outgoingText, outgoingOpacity, outgoingOffsetDp);
        DrawLayer(e.Graphics, incomingText, incomingOpacity, incomingOffsetDp);
    }

    private void StartTransition(string previousText, float previousOffsetDp, string nextText, Bitmap? previousSnapshot)
    {
        incomingText = nextText;
        if (!CanAnimate() || string.Equals(previousText, nextText, StringComparison.Ordinal))
        {
            previousSnapshot?.Dispose();
            SettleVisual();
            return;
        }

        outgoingSnapshot = previousSnapshot;
        outgoingText = previousText;
        // A snapshot already contains the exact interrupted alpha mix. When a
        // bitmap cannot be captured, promote the dominant live layer to full
        // opacity so a rapid A→B→C update still never produces an empty frame.
        outgoingOpacity = 1f;
        outgoingOffsetDp = previousSnapshot is null ? previousOffsetDp : 0f;
        incomingOpacity = 0f;
        incomingOffsetDp = travelDp;

        var startOutgoingOpacity = outgoingOpacity;
        var startOutgoingOffsetDp = outgoingOffsetDp;
        var transitionTravelDp = travelDp;
        transitionMotion = MotionClock.Animate(
            this,
            TransitionDurationMs,
            progress =>
            {
                outgoingOpacity = MotionClock.Lerp(startOutgoingOpacity, 0f, progress);
                outgoingOffsetDp = MotionClock.Lerp(startOutgoingOffsetDp, -transitionTravelDp, progress);
                incomingOpacity = progress;
                incomingOffsetDp = MotionClock.Lerp(transitionTravelDp, 0f, progress);
                Invalidate();
            },
            MotionEasing.EaseOutCubic,
            completed: SettleVisual);
    }

    private bool CanAnimate() =>
        IsHandleCreated &&
        Visible &&
        !DesignMode &&
        !MotionClock.IsReduced;

    private void DrawLayer(Graphics graphics, string text, float opacity, float offsetDp)
    {
        if (string.IsNullOrEmpty(text) || opacity <= 0f || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;

        var alpha = (int)Math.Round(textColor.A * Math.Clamp(opacity, 0f, 1f));
        if (alpha <= 0) return;

        using var brush = new SolidBrush(Color.FromArgb(alpha, textColor));
        using var format = CreateStringFormat();
        var offsetPixels = offsetDp * this.Scale();
        var bounds = new RectangleF(0f, offsetPixels, ClientSize.Width, ClientSize.Height);
        graphics.DrawString(text, Font, brush, bounds, format);
    }

    private void DrawSnapshot(Graphics graphics, Image snapshot, float opacity, float offsetDp)
    {
        if (opacity <= 0f) return;

        using var attributes = new ImageAttributes();
        var matrix = new ColorMatrix { Matrix33 = Math.Clamp(opacity, 0f, 1f) };
        attributes.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
        var offsetPixels = offsetDp * this.Scale();
        graphics.DrawImage(
            snapshot,
            new Rectangle(0, (int)Math.Round(offsetPixels), snapshot.Width, snapshot.Height),
            0f,
            0f,
            snapshot.Width,
            snapshot.Height,
            GraphicsUnit.Pixel,
            attributes);
    }

    private Bitmap? CaptureCurrentVisual()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return null;

        var snapshot = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppPArgb);
        snapshot.SetResolution(DeviceDpi, DeviceDpi);
        using var graphics = Graphics.FromImage(snapshot);
        graphics.Clear(Color.Transparent);
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        if (outgoingSnapshot is not null) DrawSnapshot(graphics, outgoingSnapshot, outgoingOpacity, outgoingOffsetDp);
        else DrawLayer(graphics, outgoingText, outgoingOpacity, outgoingOffsetDp);
        DrawLayer(graphics, incomingText, incomingOpacity, incomingOffsetDp);
        return snapshot;
    }

    private StringFormat CreateStringFormat()
    {
        var format = new StringFormat(StringFormat.GenericDefault)
        {
            Alignment = textAlign switch
            {
                ContentAlignment.TopCenter or ContentAlignment.MiddleCenter or ContentAlignment.BottomCenter => StringAlignment.Center,
                ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight => StringAlignment.Far,
                _ => StringAlignment.Near,
            },
            LineAlignment = textAlign switch
            {
                ContentAlignment.MiddleLeft or ContentAlignment.MiddleCenter or ContentAlignment.MiddleRight => StringAlignment.Center,
                ContentAlignment.BottomLeft or ContentAlignment.BottomCenter or ContentAlignment.BottomRight => StringAlignment.Far,
                _ => StringAlignment.Near,
            },
            FormatFlags = StringFormatFlags.LineLimit | StringFormatFlags.NoWrap,
            HotkeyPrefix = HotkeyPrefix.None,
            Trimming = StringTrimming.EllipsisCharacter,
        };
        return format;
    }

    private void SettleVisual()
    {
        CancelTransition();
        outgoingText = string.Empty;
        outgoingOpacity = 0f;
        outgoingOffsetDp = 0f;
        incomingText = ToSingleLine(base.Text);
        incomingOpacity = 1f;
        incomingOffsetDp = 0f;
        Invalidate();
    }

    private void CancelTransition()
    {
        transitionMotion?.Dispose();
        transitionMotion = null;
        outgoingSnapshot?.Dispose();
        outgoingSnapshot = null;
    }

    private static string ToSingleLine(string? text) => string.IsNullOrEmpty(text) ? string.Empty : text.ReplaceLineEndings(" ");

    protected override void Dispose(bool disposing)
    {
        if (disposing) CancelTransition();
        base.Dispose(disposing);
    }
}
