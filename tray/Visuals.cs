using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CodexPresence;

public static class Visuals
{
    public static readonly Color Background = Color.FromArgb(20, 20, 25);
    public static readonly Color Surface = Color.FromArgb(30, 30, 38);
    public static readonly Color SurfaceRaised = Color.FromArgb(39, 39, 49);
    public static readonly Color Accent = Color.FromArgb(126, 111, 255);
    public static readonly Color AccentBright = Color.FromArgb(160, 147, 255);
    public static readonly Color Text = Color.FromArgb(244, 244, 248);
    public static readonly Color Muted = Color.FromArgb(165, 165, 178);
    public static readonly Color Success = Color.FromArgb(76, 217, 142);
    public static readonly Color Danger = Color.FromArgb(255, 105, 120);

    public static Icon CreateIcon(int size = 64)
    {
        using var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var background = new LinearGradientBrush(new Rectangle(0, 0, size, size), AccentBright, Color.FromArgb(92, 90, 238), 135f);
        graphics.FillRoundedRectangle(background, new RectangleF(1, 1, size - 2, size - 2), size * .24f);
        using var pen = new Pen(Color.White, Math.Max(2, size / 15f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        graphics.DrawLines(pen, [new PointF(size * .25f, size * .35f), new PointF(size * .39f, size * .5f), new PointF(size * .25f, size * .65f)]);
        graphics.DrawLine(pen, size * .48f, size * .65f, size * .73f, size * .65f);
        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    public static Button Button(string text, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = false,
            Height = 38,
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Accent : SurfaceRaised,
            ForeColor = Text,
            Cursor = Cursors.Hand,
            Padding = new Padding(12, 0, 12, 0),
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    public static Label Label(string text, float size = 9f, bool muted = false, FontStyle style = FontStyle.Regular) => new()
    {
        Text = text,
        ForeColor = muted ? Muted : Text,
        Font = new Font("Segoe UI", size, style),
        AutoSize = true,
    };

    private static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF rectangle, float radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool DestroyIcon(IntPtr handle);
}
