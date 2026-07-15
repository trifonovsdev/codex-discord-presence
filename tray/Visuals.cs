using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CodexPresence;

public static class Visuals
{
    public static readonly Color Canvas = Color.FromArgb(13, 13, 13);
    public static readonly Color Background = Color.FromArgb(18, 18, 18);
    public static readonly Color Surface = Color.FromArgb(24, 24, 24);
    public static readonly Color SurfaceRaised = Color.FromArgb(31, 31, 31);
    public static readonly Color SurfaceHover = Color.FromArgb(39, 39, 39);
    public static readonly Color Border = Color.FromArgb(48, 48, 48);
    public static readonly Color BorderSoft = Color.FromArgb(38, 38, 38);
    public static readonly Color Accent = Color.FromArgb(238, 238, 238);
    public static readonly Color AccentText = Color.FromArgb(14, 14, 14);
    public static readonly Color Text = Color.FromArgb(242, 242, 242);
    public static readonly Color TextSecondary = Color.FromArgb(183, 183, 183);
    public static readonly Color Muted = Color.FromArgb(132, 132, 132);
    public static readonly Color Success = Color.FromArgb(54, 211, 153);
    public static readonly Color SuccessSurface = Color.FromArgb(20, 55, 45);
    public static readonly Color Danger = Color.FromArgb(248, 113, 113);
    public static readonly Color DangerSurface = Color.FromArgb(64, 31, 31);

    public static Font Font(float size, FontStyle style = FontStyle.Regular) => new("Segoe UI Variable Text", size, style);
    public static Font DisplayFont(float size, FontStyle style = FontStyle.Regular) => new("Segoe UI Variable Display", size, style);

    public static Icon CreateIcon(int size = 64)
    {
        using var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(Color.Transparent);

        var inset = Math.Max(1f, size * .035f);
        using var tile = new SolidBrush(Color.FromArgb(17, 17, 17));
        using var outline = new Pen(Color.FromArgb(238, 238, 238), Math.Max(1.4f, size * .035f));
        using var glyph = new Pen(Color.FromArgb(238, 238, 238), Math.Max(2f, size * .065f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        graphics.FillRoundedRectangle(tile, new RectangleF(inset, inset, size - inset * 2, size - inset * 2), size * .22f);
        graphics.DrawRoundedRectangle(outline, new RectangleF(inset + 1, inset + 1, size - inset * 2 - 2, size - inset * 2 - 2), size * .2f);
        graphics.DrawLines(glyph, new PointF[] { new(size * .27f, size * .34f), new(size * .41f, size * .5f), new(size * .27f, size * .66f) });
        graphics.DrawLine(glyph, size * .5f, size * .66f, size * .72f, size * .66f);
        using var live = new SolidBrush(Success);
        graphics.FillEllipse(live, size * .69f, size * .21f, size * .11f, size * .11f);

        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    public static ModernButton Button(string text, ButtonKind kind = ButtonKind.Secondary, string? icon = null) => new()
    {
        Text = text,
        Kind = kind,
        IconGlyph = icon,
        Height = 40,
    };

    public static Label Label(string text, float size = 9f, bool muted = false, FontStyle style = FontStyle.Regular) => new()
    {
        Text = text,
        ForeColor = muted ? TextSecondary : Text,
        Font = Font(size, style),
        AutoSize = true,
        BackColor = Color.Transparent,
    };

    public static Label Eyebrow(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        ForeColor = Muted,
        Font = Font(8.5f, FontStyle.Bold),
        AutoSize = true,
        BackColor = Color.Transparent,
    };

    public static ModernSelect Select(IEnumerable<string> values, int width = 210) => new(values) { Width = width };

    public static GraphicsPath RoundedPath(RectangleF rectangle, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF rectangle, float radius)
    {
        using var path = RoundedPath(rectangle, radius);
        graphics.FillPath(brush, path);
    }

    public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, RectangleF rectangle, float radius)
    {
        using var path = RoundedPath(rectangle, radius);
        graphics.DrawPath(pen, path);
    }

    public static void ApplyWindowStyle(Form form)
    {
        if (!OperatingSystem.IsWindows()) return;
        var dark = 1;
        var corner = 2;
        _ = DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(form.Handle, 33, ref corner, sizeof(int));
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool DestroyIcon(IntPtr handle);
}
