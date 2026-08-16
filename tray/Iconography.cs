using System.Drawing.Drawing2D;

namespace CodexPresence;

public enum UiIcon
{
    Brand,
    Pause,
    Play,
    Settings,
    Diagnostics,
    General,
    Privacy,
    Remote,
    Task,
    Add,
    Remove,
    Exit,
    Refresh,
    Copy,
    ChevronDown,
    Check,
    Warning,
    Info,
    File,
}

/// <summary>
/// Small optical icons drawn from deterministic vector geometry. They do not
/// depend on private-use font glyphs, so Windows font substitution cannot turn
/// a command into tofu or an unrelated symbol.
/// </summary>
public static class UiIcons
{
    private delegate void IconPainter(Graphics graphics, Pen pen, Brush brush);

    public static Bitmap RenderBitmap(UiIcon icon, int size, Color color)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(Color.Transparent);
        Draw(graphics, icon, new RectangleF(0, 0, size, size), color);
        return bitmap;
    }

    public static void Draw(Graphics graphics, UiIcon icon, RectangleF bounds, Color color, float strokeWidth = 1.75f)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        IconPainter painter = icon switch
        {
            UiIcon.Brand => DrawBrand,
            UiIcon.Pause => DrawPause,
            UiIcon.Play => DrawPlay,
            UiIcon.Settings => DrawSettings,
            UiIcon.Diagnostics => DrawDiagnostics,
            UiIcon.General => DrawGeneral,
            UiIcon.Privacy => DrawPrivacy,
            UiIcon.Remote => DrawRemote,
            UiIcon.Task => DrawTask,
            UiIcon.Add => DrawAdd,
            UiIcon.Remove => DrawRemove,
            UiIcon.Exit => DrawExit,
            UiIcon.Refresh => DrawRefresh,
            UiIcon.Copy => DrawCopy,
            UiIcon.ChevronDown => DrawChevronDown,
            UiIcon.Check => DrawCheck,
            UiIcon.Warning => DrawWarning,
            UiIcon.Info => DrawInfo,
            UiIcon.File => DrawFile,
            _ => DrawInfo,
        };

        var state = graphics.Save();
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var side = Math.Min(bounds.Width, bounds.Height);
            graphics.TranslateTransform(bounds.X + (bounds.Width - side) / 2f, bounds.Y + (bounds.Height - side) / 2f);
            graphics.ScaleTransform(side / 24f, side / 24f);

            using var pen = new Pen(color, strokeWidth)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            using var brush = new SolidBrush(color);
            painter(graphics, pen, brush);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private static void DrawBrand(Graphics graphics, Pen pen, Brush brush)
    {
        graphics.DrawLines(pen, [new PointF(4.5f, 6.5f), new PointF(9.8f, 12f), new PointF(4.5f, 17.5f)]);
        graphics.DrawLine(pen, 12.5f, 17.5f, 19.5f, 17.5f);
    }

    private static void DrawPause(Graphics graphics, Pen pen, Brush brush)
    {
        graphics.FillRoundedRectangle(brush, new RectangleF(7f, 5f, 3.5f, 14f), 1.2f);
        graphics.FillRoundedRectangle(brush, new RectangleF(13.5f, 5f, 3.5f, 14f), 1.2f);
    }

    private static void DrawPlay(Graphics graphics, Pen pen, Brush brush)
    {
        using var path = new GraphicsPath();
        path.AddPolygon([new PointF(8f, 5.5f), new PointF(18f, 12f), new PointF(8f, 18.5f)]);
        graphics.FillPath(brush, path);
    }

    private static void DrawSettings(Graphics graphics, Pen pen, Brush brush)
    {
        graphics.DrawEllipse(pen, 8.25f, 8.25f, 7.5f, 7.5f);
        graphics.DrawEllipse(pen, 10.6f, 10.6f, 2.8f, 2.8f);
        for (var index = 0; index < 8; index++)
        {
            var angle = MathF.PI * index / 4f;
            graphics.DrawLine(
                pen,
                12f + MathF.Cos(angle) * 5.4f,
                12f + MathF.Sin(angle) * 5.4f,
                12f + MathF.Cos(angle) * 8f,
                12f + MathF.Sin(angle) * 8f);
        }
    }

    private static void DrawDiagnostics(Graphics graphics, Pen pen, Brush brush)
    {
        graphics.DrawEllipse(pen, 3.5f, 3.5f, 17f, 17f);
        graphics.DrawLines(pen,
        [
            new PointF(6.5f, 12f),
            new PointF(9.1f, 12f),
            new PointF(10.8f, 8.5f),
            new PointF(13.2f, 15.5f),
            new PointF(15f, 12f),
            new PointF(17.5f, 12f),
        ]);
    }

    private static void DrawGeneral(Graphics graphics, Pen pen, Brush brush)
    {
        graphics.DrawLine(pen, 4f, 7f, 20f, 7f);
        graphics.DrawLine(pen, 4f, 12f, 20f, 12f);
        graphics.DrawLine(pen, 4f, 17f, 20f, 17f);
        graphics.FillEllipse(brush, 7f, 5f, 4f, 4f);
        graphics.FillEllipse(brush, 14f, 10f, 4f, 4f);
        graphics.FillEllipse(brush, 9f, 15f, 4f, 4f);
    }

    private static void DrawPrivacy(Graphics graphics, Pen pen, Brush brush)
    {
        using var path = new GraphicsPath();
        path.AddLines(
        [
            new PointF(12f, 3.5f),
            new PointF(19f, 6.4f),
            new PointF(18.1f, 14.3f),
            new PointF(15.8f, 18.1f),
            new PointF(12f, 20.5f),
            new PointF(8.2f, 18.1f),
            new PointF(5.9f, 14.3f),
            new PointF(5f, 6.4f),
            new PointF(12f, 3.5f),
        ]);
        graphics.DrawPath(pen, path);
        graphics.DrawLines(pen, [new PointF(8.5f, 11.8f), new PointF(10.8f, 14.1f), new PointF(15.8f, 9.1f)]);
    }

    private static void DrawRemote(Graphics graphics, Pen pen, Brush brush)
    {
        graphics.DrawRoundedRectangle(pen, new RectangleF(3.5f, 5f, 17f, 14f), 2.5f);
        graphics.DrawLines(pen, [new PointF(7f, 9f), new PointF(10f, 12f), new PointF(7f, 15f)]);
        graphics.DrawLine(pen, 12.5f, 15f, 17f, 15f);
    }

    private static void DrawTask(Graphics graphics, Pen pen, Brush brush)
    {
        graphics.DrawRoundedRectangle(pen, new RectangleF(5f, 5f, 14f, 16f), 2f);
        graphics.DrawRoundedRectangle(pen, new RectangleF(8f, 3f, 8f, 4f), 1.5f);
        graphics.DrawLine(pen, 8f, 11f, 16f, 11f);
        graphics.DrawLine(pen, 8f, 15f, 14f, 15f);
    }

    private static void DrawAdd(Graphics graphics, Pen pen, Brush brush)
    {
        graphics.DrawLine(pen, 5f, 12f, 19f, 12f);
        graphics.DrawLine(pen, 12f, 5f, 12f, 19f);
    }

    private static void DrawRemove(Graphics graphics, Pen pen, Brush brush) => graphics.DrawLine(pen, 5f, 12f, 19f, 12f);

    private static void DrawExit(Graphics graphics, Pen pen, Brush brush)
    {
        graphics.DrawLines(pen, [new PointF(10f, 5f), new PointF(5f, 5f), new PointF(5f, 19f), new PointF(10f, 19f)]);
        graphics.DrawLine(pen, 9f, 12f, 20f, 12f);
        graphics.DrawLines(pen, [new PointF(16f, 8f), new PointF(20f, 12f), new PointF(16f, 16f)]);
    }

    private static void DrawRefresh(Graphics graphics, Pen pen, Brush brush)
    {
        graphics.DrawArc(pen, 4f, 4f, 16f, 16f, -42f, 286f);
        graphics.DrawLines(pen, [new PointF(15.2f, 4.9f), new PointF(19.7f, 4.8f), new PointF(19.4f, 9.2f)]);
    }

    private static void DrawCopy(Graphics graphics, Pen pen, Brush brush)
    {
        graphics.DrawRoundedRectangle(pen, new RectangleF(8f, 7f, 11f, 13f), 2f);
        graphics.DrawLines(pen, [new PointF(7f, 17f), new PointF(5f, 17f), new PointF(5f, 4f), new PointF(16f, 4f), new PointF(16f, 6f)]);
    }

    private static void DrawChevronDown(Graphics graphics, Pen pen, Brush brush) =>
        graphics.DrawLines(pen, [new PointF(6f, 9f), new PointF(12f, 15f), new PointF(18f, 9f)]);

    private static void DrawCheck(Graphics graphics, Pen pen, Brush brush) =>
        graphics.DrawLines(pen, [new PointF(5f, 12.5f), new PointF(10f, 17f), new PointF(19f, 7f)]);

    private static void DrawWarning(Graphics graphics, Pen pen, Brush brush)
    {
        graphics.DrawPolygon(pen, [new PointF(12f, 3.5f), new PointF(21f, 20f), new PointF(3f, 20f)]);
        graphics.DrawLine(pen, 12f, 8.5f, 12f, 14f);
        graphics.FillEllipse(brush, 10.8f, 16.4f, 2.4f, 2.4f);
    }

    private static void DrawInfo(Graphics graphics, Pen pen, Brush brush)
    {
        graphics.DrawEllipse(pen, 3.5f, 3.5f, 17f, 17f);
        graphics.FillEllipse(brush, 10.8f, 7f, 2.4f, 2.4f);
        graphics.DrawLine(pen, 12f, 11f, 12f, 17f);
    }

    private static void DrawFile(Graphics graphics, Pen pen, Brush brush)
    {
        graphics.DrawLines(pen,
        [
            new PointF(6f, 3.5f),
            new PointF(14f, 3.5f),
            new PointF(19f, 8.5f),
            new PointF(19f, 20.5f),
            new PointF(6f, 20.5f),
            new PointF(6f, 3.5f),
        ]);
        graphics.DrawLines(pen, [new PointF(14f, 3.5f), new PointF(14f, 8.5f), new PointF(19f, 8.5f)]);
    }
}

public sealed class IconView : Control
{
    private UiIcon icon;
    private Color iconColor = Visuals.TextSecondary;

    public UiIcon Icon
    {
        get => icon;
        set { if (icon == value) return; icon = value; Invalidate(); }
    }

    public Color IconColor
    {
        get => iconColor;
        set { if (iconColor == value) return; iconColor = value; Invalidate(); }
    }

    public IconView(UiIcon icon)
    {
        this.icon = icon;
        Size = new Size(20, 20);
        TabStop = false;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw,
            true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        UiIcons.Draw(e.Graphics, Icon, new RectangleF(0, 0, Width, Height), IconColor);
    }
}
