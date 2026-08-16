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
    Refresh,
    Copy,
    ChevronDown,
    Check,
    Warning,
    Info,
    File,
}

/// <summary>Small, dependency-free monoline icons drawn from one 24-unit grid.</summary>
public static class UiIcons
{
    public static Bitmap RenderBitmap(UiIcon icon, int size, Color color)
    {
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
        var state = graphics.Save();
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.TranslateTransform(bounds.X, bounds.Y);
            graphics.ScaleTransform(bounds.Width / 24f, bounds.Height / 24f);

            using var pen = new Pen(color, strokeWidth)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };

            switch (icon)
            {
                case UiIcon.Brand:
                    graphics.DrawRoundedRectangle(pen, new RectangleF(2.5f, 2.5f, 19, 19), 5.5f);
                    graphics.DrawLines(pen, [new PointF(7, 8), new PointF(11, 12), new PointF(7, 16)]);
                    graphics.DrawLine(pen, 13.5f, 16, 18, 16);
                    break;
                case UiIcon.Pause:
                    graphics.DrawLine(pen, 8.5f, 6, 8.5f, 18);
                    graphics.DrawLine(pen, 15.5f, 6, 15.5f, 18);
                    break;
                case UiIcon.Play:
                    DrawPath(graphics, pen, [new(8, 5.5f), new(18, 12), new(8, 18.5f)], close: true);
                    break;
                case UiIcon.Settings:
                case UiIcon.General:
                    Slider(graphics, pen, 6, 9);
                    Slider(graphics, pen, 12, 15);
                    Slider(graphics, pen, 18, 11);
                    break;
                case UiIcon.Diagnostics:
                    graphics.DrawEllipse(pen, 3.5f, 3.5f, 17, 17);
                    graphics.DrawLines(pen, [new PointF(7.5f, 12), new PointF(10.5f, 15), new PointF(16.5f, 8.5f)]);
                    break;
                case UiIcon.Privacy:
                    graphics.DrawRoundedRectangle(pen, new RectangleF(5, 10, 14, 10), 2.5f);
                    graphics.DrawArc(pen, 8, 4, 8, 11, 180, 180);
                    graphics.DrawLine(pen, 12, 14, 12, 17);
                    break;
                case UiIcon.Remote:
                    graphics.DrawEllipse(pen, 4, 13, 6, 6);
                    graphics.DrawEllipse(pen, 14, 5, 6, 6);
                    graphics.DrawLine(pen, 9, 14, 15, 10);
                    graphics.DrawLine(pen, 8, 13, 8, 8);
                    graphics.DrawLine(pen, 8, 8, 13, 8);
                    break;
                case UiIcon.Task:
                    graphics.DrawRoundedRectangle(pen, new RectangleF(4, 5, 16, 14), 3);
                    graphics.DrawLine(pen, 8, 9, 16, 9);
                    graphics.DrawLine(pen, 8, 13, 14, 13);
                    graphics.DrawLine(pen, 8, 17, 12, 17);
                    break;
                case UiIcon.Add:
                    graphics.DrawLine(pen, 12, 5, 12, 19);
                    graphics.DrawLine(pen, 5, 12, 19, 12);
                    break;
                case UiIcon.Remove:
                    graphics.DrawLine(pen, 5, 12, 19, 12);
                    break;
                case UiIcon.Refresh:
                    graphics.DrawArc(pen, 4, 4, 16, 16, 205, 245);
                    graphics.DrawLines(pen, [new PointF(18.5f, 5), new PointF(19, 10), new PointF(14, 9)]);
                    break;
                case UiIcon.Copy:
                    graphics.DrawRoundedRectangle(pen, new RectangleF(8, 7, 11, 12), 2);
                    graphics.DrawLines(pen, [new PointF(15, 7), new PointF(15, 5), new PointF(5, 5), new PointF(5, 16), new PointF(8, 16)]);
                    break;
                case UiIcon.ChevronDown:
                    graphics.DrawLines(pen, [new PointF(6, 9), new PointF(12, 15), new PointF(18, 9)]);
                    break;
                case UiIcon.Check:
                    graphics.DrawLines(pen, [new PointF(5, 12), new PointF(10, 17), new PointF(19, 7)]);
                    break;
                case UiIcon.Warning:
                    DrawPath(graphics, pen, [new(12, 3.5f), new(21, 19.5f), new(3, 19.5f)], close: true);
                    graphics.DrawLine(pen, 12, 9, 12, 14);
                    graphics.DrawEllipse(pen, 11.8f, 17, .4f, .4f);
                    break;
                case UiIcon.Info:
                    graphics.DrawEllipse(pen, 3.5f, 3.5f, 17, 17);
                    graphics.DrawLine(pen, 12, 10.5f, 12, 17);
                    graphics.DrawEllipse(pen, 11.8f, 7, .4f, .4f);
                    break;
                case UiIcon.File:
                    DrawPath(graphics, pen, [new(6, 3.5f), new(14, 3.5f), new(19, 8.5f), new(19, 20.5f), new(6, 20.5f)], close: true);
                    graphics.DrawLines(pen, [new PointF(14, 3.5f), new PointF(14, 8.5f), new PointF(19, 8.5f)]);
                    break;
            }
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private static void Slider(Graphics graphics, Pen pen, float y, float knobX)
    {
        graphics.DrawLine(pen, 4, y, 20, y);
        using var fill = new SolidBrush(Color.FromArgb(255, pen.Color));
        graphics.FillEllipse(fill, knobX - 1.8f, y - 1.8f, 3.6f, 3.6f);
    }

    private static void DrawPath(Graphics graphics, Pen pen, PointF[] points, bool close)
    {
        using var path = new GraphicsPath();
        path.AddLines(points);
        if (close) path.CloseFigure();
        graphics.DrawPath(pen, path);
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
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Visuals.Background);
        UiIcons.Draw(e.Graphics, Icon, new RectangleF(0, 0, Width, Height), IconColor);
    }
}
