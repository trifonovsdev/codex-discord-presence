using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace CodexPresence;

public static class Visuals
{
    public static readonly Color Canvas = Color.FromArgb(9, 12, 16);
    public static readonly Color Background = Color.FromArgb(13, 17, 22);
    public static readonly Color Surface = Color.FromArgb(18, 23, 30);
    public static readonly Color SurfaceRaised = Color.FromArgb(25, 31, 40);
    public static readonly Color SurfaceHover = Color.FromArgb(33, 41, 52);
    public static readonly Color Border = Color.FromArgb(50, 60, 74);
    public static readonly Color BorderSoft = Color.FromArgb(36, 44, 55);
    public static readonly Color Accent = Color.FromArgb(239, 243, 247);
    public static readonly Color AccentText = Color.FromArgb(11, 15, 20);
    public static readonly Color Text = Color.FromArgb(242, 245, 248);
    public static readonly Color TextSecondary = Color.FromArgb(177, 187, 199);
    public static readonly Color Muted = Color.FromArgb(145, 157, 171);
    public static readonly Color Success = Color.FromArgb(91, 219, 165);
    public static readonly Color SuccessSurface = Color.FromArgb(18, 55, 45);
    public static readonly Color Danger = Color.FromArgb(247, 126, 126);
    public static readonly Color DangerSurface = Color.FromArgb(62, 30, 35);
    public static readonly Color FocusRing = Color.FromArgb(137, 194, 255);

    private static readonly ConcurrentDictionary<(string Family, float Size, FontStyle Style), Font> FontCache = new();
    private static readonly Lazy<string> TextFamily = new(() => ResolveFamily("Segoe UI Variable Text", "Segoe UI"));
    private static readonly Lazy<string> DisplayFamily = new(() => ResolveFamily("Segoe UI Variable Display", "Segoe UI Variable Text", "Segoe UI"));
    private static readonly Lazy<string> MonoFamily = new(() => ResolveFamily("Cascadia Mono", "Consolas", "Courier New"));
    private static readonly Lazy<Icon> SharedIcon = new(() => RenderIcon(64));

    /// <summary>
    /// Picks the first font family that is actually installed. The variable
    /// Segoe families only ship with Windows 11: without this probe, a
    /// Windows 10 machine silently falls back to Microsoft Sans Serif and the
    /// whole UI loses its typography.
    /// </summary>
    private static string ResolveFamily(params string[] candidates)
    {
        using var installed = new InstalledFontCollection();
        var available = installed.Families.Select(family => family.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return candidates.FirstOrDefault(available.Contains) ?? SystemFonts.MessageBoxFont?.FontFamily.Name ?? "Segoe UI";
    }

    /// <summary>Fonts are cached and shared: every call used to allocate a GDI handle that was never released.</summary>
    private static Font Cached(string family, float size, FontStyle style) =>
        FontCache.GetOrAdd((family, size, style), key => new Font(key.Family, key.Size, key.Style, GraphicsUnit.Point));

    public static Font Font(float size, FontStyle style = FontStyle.Regular) => Cached(TextFamily.Value, size, style);
    public static Font DisplayFont(float size, FontStyle style = FontStyle.Regular) => Cached(DisplayFamily.Value, size, style);
    public static Font MonoFont(float size, FontStyle style = FontStyle.Regular) => Cached(MonoFamily.Value, size, style);

    /// <summary>Device pixels per layout unit, so custom painting stays correct above 100% scaling.</summary>
    public static float Scale(this Control control) => control.DeviceDpi / 96f;
    public static int Dp(this Control control, float value) => (int)Math.Round(value * control.Scale());

    public static Icon AppIcon => SharedIcon.Value;

    private static Icon RenderIcon(int size)
    {
        using var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(Color.Transparent);

        var inset = Math.Max(1f, size * .035f);
        using var tile = new SolidBrush(Canvas);
        graphics.FillRoundedRectangle(tile, new RectangleF(inset, inset, size - inset * 2, size - inset * 2), size * .22f);
        UiIcons.Draw(graphics, UiIcon.Brand, new RectangleF(inset + size * .08f, inset + size * .08f, size - inset * 2 - size * .16f, size - inset * 2 - size * .16f), Text);
        using var live = new SolidBrush(Success);
        graphics.FillEllipse(live, size * .69f, size * .21f, size * .11f, size * .11f);

        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    public static ModernButton Button(string text, ButtonKind kind = ButtonKind.Secondary, UiIcon? icon = null) => new()
    {
        Text = text,
        Kind = kind,
        Icon = icon,
        Height = 40,
    };

    public static Label Label(string text, float size = 9f, bool muted = false, FontStyle style = FontStyle.Regular) => new()
    {
        Text = text,
        ForeColor = muted ? TextSecondary : Text,
        Font = Font(size, style),
        AutoSize = true,
        BackColor = Color.Transparent,
        UseMnemonic = false,
    };

    public static Label Eyebrow(string text) => new()
    {
        Text = text,
        ForeColor = Muted,
        Font = Font(8.5f, FontStyle.Bold),
        AutoSize = true,
        BackColor = Color.Transparent,
        UseMnemonic = false,
    };

    public static Label Heading(string text, float size = 20f) => new()
    {
        Text = text,
        ForeColor = Text,
        Font = DisplayFont(size, FontStyle.Bold),
        AutoSize = true,
        BackColor = Color.Transparent,
        UseMnemonic = false,
    };

    public static ModernSelect Select(IEnumerable<string> values, int width = 210) => new(values) { Width = width };

    public static Color Blend(Color from, Color to, float amount)
    {
        var t = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            (int)(from.A + (to.A - from.A) * t),
            (int)(from.R + (to.R - from.R) * t),
            (int)(from.G + (to.G - from.G) * t),
            (int)(from.B + (to.B - from.B) * t));
    }

    public static GraphicsPath RoundedPath(RectangleF rectangle, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
        if (diameter <= 0)
        {
            path.AddRectangle(rectangle);
            return path;
        }
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

    /// <summary>Applies the Windows 11 dark, rounded, dark-bordered window frame.</summary>
    public static void ApplyWindowStyle(Form form)
    {
        if (!OperatingSystem.IsWindows() || !form.IsHandleCreated) return;
        var dark = 1;
        var corner = 2;
        var border = ColorRef(Border);
        _ = DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(form.Handle, 33, ref corner, sizeof(int));
        _ = DwmSetWindowAttribute(form.Handle, 34, ref border, sizeof(int));
    }

    private static int ColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool DestroyIcon(IntPtr handle);
}
