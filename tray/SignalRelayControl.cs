using System.Drawing.Drawing2D;

namespace CodexPresence;

public enum SignalRelayStatus
{
    Offline,
    Pending,
    Failed,
    Paused,
    Live,
}

/// <summary>
/// Renders the publication route from Codex through the local daemon to Discord.
/// Call <see cref="Publish"/> after a presence payload is accepted for delivery.
/// </summary>
public sealed class SignalRelayControl : Control
{
    private const int TravelDurationMs = 260;
    private const int AcknowledgementDurationMs = 140;

    private IDisposable? travelMotion;
    private IDisposable? acknowledgementMotion;
    private SignalRelayStatus status;
    private float packetProgress;
    private float acknowledgementProgress;
    private bool isPublishing;

    public SignalRelayStatus Status
    {
        get => status;
        set
        {
            if (status == value) return;

            status = value;
            if (status != SignalRelayStatus.Live) CancelPublish();
            UpdateAccessibility();
            Invalidate();
        }
    }

    public bool IsPublishing => isPublishing;

    public event EventHandler? PublishCompleted;

    public SignalRelayControl()
    {
        Size = new Size(540, 104);
        MinimumSize = new Size(360, 88);
        BackColor = Color.Transparent;
        TabStop = false;
        AccessibleRole = AccessibleRole.Grouping;
        AccessibleName = "Presence signal route";
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.UserPaint,
            true);
        UpdateAccessibility();
    }

    /// <summary>
    /// Plays one delivery from Codex to Discord. Calls made while paused or
    /// offline are ignored because no presence payload is being published.
    /// </summary>
    public void Publish()
    {
        if (Status != SignalRelayStatus.Live || IsDisposed) return;

        CancelPublish();
        isPublishing = true;

        if (MotionClock.IsReduced || !IsHandleCreated || !Visible)
        {
            packetProgress = 1f;
            acknowledgementProgress = 1f;
            CompletePublish();
            return;
        }

        travelMotion = MotionClock.Animate(
            this,
            TravelDurationMs,
            value =>
            {
                packetProgress = value;
                Invalidate();
            },
            MotionEasing.EaseInOutCubic,
            completed: StartAcknowledgement);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var layout = MeasureLayout();
        var colors = ResolveColors();
        DrawRoute(graphics, layout, colors);
        DrawNodes(graphics, layout, colors);
        DrawLabels(graphics, layout, colors);
        DrawStatus(graphics, layout, colors);

        if (isPublishing) DrawPublishMotion(graphics, layout, colors);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible && isPublishing) CompletePublish();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            travelMotion?.Dispose();
            acknowledgementMotion?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void StartAcknowledgement()
    {
        travelMotion = null;
        acknowledgementMotion = MotionClock.Animate(
            this,
            AcknowledgementDurationMs,
            value =>
            {
                acknowledgementProgress = value;
                Invalidate();
            },
            MotionEasing.EaseOutCubic,
            completed: CompletePublish);
    }

    private void CompletePublish()
    {
        if (!isPublishing) return;

        travelMotion?.Dispose();
        acknowledgementMotion?.Dispose();
        travelMotion = null;
        acknowledgementMotion = null;
        packetProgress = 0f;
        acknowledgementProgress = 0f;
        isPublishing = false;
        Invalidate();
        PublishCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void CancelPublish()
    {
        travelMotion?.Dispose();
        acknowledgementMotion?.Dispose();
        travelMotion = null;
        acknowledgementMotion = null;
        packetProgress = 0f;
        acknowledgementProgress = 0f;
        isPublishing = false;
    }

    private RelayLayout MeasureLayout()
    {
        var nodeRadius = this.Dp(16);
        var horizontalInset = Math.Max(this.Dp(28), nodeRadius + this.Dp(8));
        var routeY = Math.Clamp(this.Dp(54), nodeRadius + this.Dp(22), Math.Max(nodeRadius + this.Dp(22), Height - this.Dp(28)));
        var sourceX = horizontalInset;
        var destinationX = Math.Max(sourceX, Width - horizontalInset);
        var daemonX = sourceX + (destinationX - sourceX) / 2f;

        return new RelayLayout(
            new PointF(sourceX, routeY),
            new PointF(daemonX, routeY),
            new PointF(destinationX, routeY),
            nodeRadius);
    }

    private RelayColors ResolveColors()
    {
        if (SystemInformation.HighContrast)
        {
            return new RelayColors(
                SystemColors.ControlText,
                SystemColors.GrayText,
                SystemColors.Control,
                SystemColors.Highlight,
                SystemColors.HighlightText);
        }

        var signal = Status switch
        {
            SignalRelayStatus.Live => Visuals.Success,
            SignalRelayStatus.Pending => Color.FromArgb(226, 183, 101),
            SignalRelayStatus.Failed => Visuals.Danger,
            SignalRelayStatus.Paused => Color.FromArgb(226, 183, 101),
            _ => Visuals.Danger,
        };
        return new RelayColors(Visuals.Text, Visuals.Border, Visuals.SurfaceRaised, signal, Visuals.Canvas);
    }

    private void DrawRoute(Graphics graphics, RelayLayout layout, RelayColors colors)
    {
        var lineStart = new PointF(layout.Source.X + layout.NodeRadius, layout.Source.Y);
        var lineEnd = new PointF(layout.Destination.X - layout.NodeRadius, layout.Destination.Y);
        using var track = new Pen(colors.Track, Math.Max(1f, this.Scale()));
        graphics.DrawLine(track, lineStart, lineEnd);

        if (Status == SignalRelayStatus.Offline) return;

        var signalAlpha = SystemInformation.HighContrast
            ? 255
            : Status == SignalRelayStatus.Live ? 210 : 118;
        using var signal = new Pen(Color.FromArgb(signalAlpha, colors.Signal), Math.Max(this.Dp(2), 1f));
        if (Status is SignalRelayStatus.Paused or SignalRelayStatus.Pending) signal.DashPattern = [2.2f, 2.8f];
        graphics.DrawLine(signal, lineStart, lineEnd);
    }

    private void DrawNodes(Graphics graphics, RelayLayout layout, RelayColors colors)
    {
        DrawNode(graphics, layout.Source, layout.NodeRadius, RelayNode.Codex, colors);
        DrawNode(graphics, layout.Daemon, layout.NodeRadius, RelayNode.Daemon, colors);
        DrawNode(graphics, layout.Destination, layout.NodeRadius, RelayNode.Discord, colors);
    }

    private void DrawNode(Graphics graphics, PointF center, float radius, RelayNode node, RelayColors colors)
    {
        var bounds = new RectangleF(center.X - radius, center.Y - radius, radius * 2, radius * 2);
        using var fill = new SolidBrush(colors.NodeFill);
        using var border = new Pen(
            Status == SignalRelayStatus.Offline ? colors.Track : Color.FromArgb(210, colors.Signal),
            Math.Max(1f, this.Scale()));
        graphics.FillEllipse(fill, bounds);
        graphics.DrawEllipse(border, bounds);

        var iconBounds = RectangleF.Inflate(bounds, -this.Dp(8), -this.Dp(8));
        var iconColor = Status == SignalRelayStatus.Offline ? colors.MutedText : colors.Foreground;
        using var iconPen = new Pen(iconColor, Math.Max(1.25f, this.Scale() * 1.5f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        switch (node)
        {
            case RelayNode.Codex:
                DrawCodexIcon(graphics, iconPen, iconBounds);
                break;
            case RelayNode.Daemon:
                DrawDaemonIcon(graphics, iconPen, iconBounds);
                break;
            case RelayNode.Discord:
                DrawDiscordIcon(graphics, iconPen, iconBounds);
                break;
        }
    }

    private void DrawLabels(Graphics graphics, RelayLayout layout, RelayColors colors)
    {
        var regionWidth = Math.Max(this.Dp(90), Width / 3);
        var top = Math.Max(0, (int)layout.Source.Y - this.Dp(38));
        var height = this.Dp(18);
        var font = Visuals.Font(8.5f, FontStyle.Bold);
        var flags = TextFormatFlags.HorizontalCenter |
                    TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPrefix |
                    TextFormatFlags.SingleLine;

        DrawNodeLabel(graphics, "CODEX", layout.Source.X, top, regionWidth, height, colors.Foreground, font, flags);
        DrawNodeLabel(graphics, "LOCAL DAEMON", layout.Daemon.X, top, regionWidth, height, colors.Foreground, font, flags);
        DrawNodeLabel(graphics, "DISCORD", layout.Destination.X, top, regionWidth, height, colors.Foreground, font, flags);
    }

    private static void DrawNodeLabel(
        Graphics graphics,
        string text,
        float centerX,
        int top,
        int width,
        int height,
        Color color,
        Font font,
        TextFormatFlags flags)
    {
        var bounds = new Rectangle((int)Math.Round(centerX - width / 2f), top, width, height);
        TextRenderer.DrawText(graphics, text, font, bounds, color, flags);
    }

    private void DrawStatus(Graphics graphics, RelayLayout layout, RelayColors colors)
    {
        var text = Status switch
        {
            SignalRelayStatus.Live => "Live",
            SignalRelayStatus.Pending => "Publishing",
            SignalRelayStatus.Failed => "Rejected",
            SignalRelayStatus.Paused => "Paused",
            _ => "Offline",
        };
        var font = Visuals.Font(8.5f, FontStyle.Bold);
        var textSize = TextRenderer.MeasureText(graphics, text, font, Size.Empty, TextFormatFlags.NoPadding);
        var markerSize = this.Dp(6);
        var gap = this.Dp(7);
        var totalWidth = markerSize + gap + textSize.Width;
        var left = (int)Math.Round(layout.Daemon.X - totalWidth / 2f);
        var top = Math.Min(Height - textSize.Height, (int)layout.Daemon.Y + this.Dp(24));
        using var marker = new SolidBrush(colors.Signal);
        graphics.FillRectangle(marker, left, top + (textSize.Height - markerSize) / 2, markerSize, markerSize);
        TextRenderer.DrawText(
            graphics,
            text,
            font,
            new Rectangle(left + markerSize + gap, top, textSize.Width, textSize.Height),
            colors.MutedText,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }

    private void DrawPublishMotion(Graphics graphics, RelayLayout layout, RelayColors colors)
    {
        if (acknowledgementProgress > 0f)
        {
            var expansion = this.Dp(11) * acknowledgementProgress;
            var radius = layout.NodeRadius + this.Dp(3) + expansion;
            var alpha = (int)Math.Round(190 * (1f - acknowledgementProgress));
            if (alpha > 0)
            {
                using var ring = new Pen(Color.FromArgb(alpha, colors.Signal), Math.Max(1f, this.Scale() * 1.5f));
                graphics.DrawEllipse(
                    ring,
                    layout.Destination.X - radius,
                    layout.Destination.Y - radius,
                    radius * 2,
                    radius * 2);
            }
        }

        var packetCenter = PointOnRoute(layout, packetProgress);
        var packetSize = this.Dp(8);
        var packetBounds = new RectangleF(
            packetCenter.X - packetSize / 2f,
            packetCenter.Y - packetSize / 2f,
            packetSize,
            packetSize);
        using var packet = new SolidBrush(colors.Signal);
        graphics.FillRoundedRectangle(packet, packetBounds, this.Dp(2));
    }

    private static PointF PointOnRoute(RelayLayout layout, float progress)
    {
        var startX = layout.Source.X + layout.NodeRadius;
        var endX = layout.Destination.X - layout.NodeRadius;
        return new PointF(MotionClock.Lerp(startX, endX, progress), layout.Source.Y);
    }

    private static void DrawCodexIcon(Graphics graphics, Pen pen, RectangleF bounds)
    {
        var left = bounds.Left;
        var top = bounds.Top;
        graphics.DrawLines(pen,
        [
            new PointF(left, top + bounds.Height * .2f),
            new PointF(left + bounds.Width * .42f, top + bounds.Height * .5f),
            new PointF(left, top + bounds.Height * .8f),
        ]);
        graphics.DrawLine(
            pen,
            left + bounds.Width * .56f,
            top + bounds.Height * .8f,
            bounds.Right,
            top + bounds.Height * .8f);
    }

    private static void DrawDaemonIcon(Graphics graphics, Pen pen, RectangleF bounds)
    {
        graphics.DrawRoundedRectangle(pen, bounds, bounds.Height * .18f);
        graphics.DrawLine(pen, bounds.Left, bounds.Top + bounds.Height * .5f, bounds.Right, bounds.Top + bounds.Height * .5f);
        using var led = new SolidBrush(pen.Color);
        var ledSize = Math.Max(1.5f, pen.Width * 1.15f);
        graphics.FillEllipse(led, bounds.Left + bounds.Width * .18f, bounds.Top + bounds.Height * .22f, ledSize, ledSize);
        graphics.FillEllipse(led, bounds.Left + bounds.Width * .18f, bounds.Top + bounds.Height * .69f, ledSize, ledSize);
    }

    private static void DrawDiscordIcon(Graphics graphics, Pen pen, RectangleF bounds)
    {
        using var path = new GraphicsPath();
        path.AddBezier(
            bounds.Left,
            bounds.Bottom,
            bounds.Left + bounds.Width * .06f,
            bounds.Top + bounds.Height * .22f,
            bounds.Left + bounds.Width * .24f,
            bounds.Top,
            bounds.Width * .5f + bounds.Left,
            bounds.Top);
        path.AddBezier(
            bounds.Width * .5f + bounds.Left,
            bounds.Top,
            bounds.Right - bounds.Width * .24f,
            bounds.Top,
            bounds.Right - bounds.Width * .06f,
            bounds.Top + bounds.Height * .22f,
            bounds.Right,
            bounds.Bottom);
        graphics.DrawPath(pen, path);
        graphics.DrawEllipse(pen, bounds.Left + bounds.Width * .26f, bounds.Top + bounds.Height * .44f, pen.Width, pen.Width);
        graphics.DrawEllipse(pen, bounds.Right - bounds.Width * .26f - pen.Width, bounds.Top + bounds.Height * .44f, pen.Width, pen.Width);
    }

    private void UpdateAccessibility()
    {
        AccessibleDescription = Status switch
        {
            SignalRelayStatus.Live => "Live. Discord acknowledged the current presence update.",
            SignalRelayStatus.Pending => "Connected. Waiting for Discord to acknowledge the current presence update.",
            SignalRelayStatus.Failed => "Discord rejected the current presence update. Open Doctor for details.",
            SignalRelayStatus.Paused => "Paused. Presence updates are not being sent to Discord.",
            _ => "Offline. The Codex presence route is disconnected.",
        };

        if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.DescriptionChange, -1);
    }

    private enum RelayNode
    {
        Codex,
        Daemon,
        Discord,
    }

    private readonly record struct RelayLayout(PointF Source, PointF Daemon, PointF Destination, float NodeRadius);

    private readonly record struct RelayColors(Color Foreground, Color Track, Color NodeFill, Color Signal, Color MutedText);
}
