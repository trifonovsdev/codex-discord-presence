using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

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
/// Renders the Windows system icon language without shipping another asset
/// library. Windows 11 uses Segoe Fluent Icons; Windows 10 falls back to the
/// codepoint-compatible Segoe MDL2 Assets family.
/// </summary>
public static class UiIcons
{
    private const string FluentFamily = "Segoe Fluent Icons";
    private const string Mdl2Family = "Segoe MDL2 Assets";

    private static readonly Lazy<string> SystemIconFamily = new(ResolveSystemIconFamily);
    private static readonly ConcurrentDictionary<int, Font> FontCache = new();
    private static readonly IReadOnlyDictionary<UiIcon, string> Glyphs = new Dictionary<UiIcon, string>
    {
        [UiIcon.Pause] = "\uE769",
        [UiIcon.Play] = "\uE768",
        [UiIcon.Settings] = "\uE713",
        [UiIcon.Diagnostics] = "\uE9D9",
        [UiIcon.General] = "\uE9E9",
        [UiIcon.Privacy] = "\uEA18",
        [UiIcon.Remote] = "\uE8AF",
        [UiIcon.Task] = "\uE9D5",
        [UiIcon.Add] = "\uE710",
        [UiIcon.Remove] = "\uE738",
        [UiIcon.Exit] = "\uE7E8",
        [UiIcon.Refresh] = "\uE72C",
        [UiIcon.Copy] = "\uE8C8",
        [UiIcon.ChevronDown] = "\uE70D",
        [UiIcon.Check] = "\uE73E",
        [UiIcon.Warning] = "\uE7BA",
        [UiIcon.Info] = "\uE946",
        [UiIcon.File] = "\uE8A5",
    };

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

        var state = graphics.Save();
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            if (icon == UiIcon.Brand)
            {
                DrawBrand(graphics, bounds, color, strokeWidth);
                return;
            }

            DrawSystemGlyph(graphics, Glyphs[icon], bounds, color);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private static void DrawSystemGlyph(Graphics graphics, string glyph, RectangleF bounds, Color color)
    {
        var emSize = Math.Max(1f, Math.Min(bounds.Width, bounds.Height) * .86f);
        var font = IconFont(emSize);
        using var brush = new SolidBrush(color);
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.Alignment = StringAlignment.Center;
        format.LineAlignment = StringAlignment.Center;
        format.FormatFlags |= StringFormatFlags.NoClip | StringFormatFlags.NoWrap;

        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.DrawString(glyph, font, brush, bounds, format);
    }

    /// <summary>
    /// A command entering a two-stage relay. Unlike the former terminal tile,
    /// the mark describes the product's actual job: carrying Codex activity
    /// toward a confirmed destination.
    /// </summary>
    private static void DrawBrand(Graphics graphics, RectangleF bounds, Color color, float strokeWidth)
    {
        graphics.TranslateTransform(bounds.X, bounds.Y);
        graphics.ScaleTransform(bounds.Width / 24f, bounds.Height / 24f);

        using var pen = new Pen(color, strokeWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        graphics.DrawLines(pen,
        [
            new PointF(4.5f, 6.5f),
            new PointF(9.8f, 12f),
            new PointF(4.5f, 17.5f),
        ]);
        graphics.DrawLine(pen, 12f, 12f, 20f, 12f);
        using var hubFill = new SolidBrush(Visuals.Canvas);
        graphics.FillEllipse(hubFill, 13.1f, 10.1f, 3.8f, 3.8f);
        graphics.DrawEllipse(pen, 13.1f, 10.1f, 3.8f, 3.8f);
        using var destination = new SolidBrush(color);
        graphics.FillEllipse(destination, 18.2f, 10.2f, 3.6f, 3.6f);
    }

    private static Font IconFont(float emSize)
    {
        // Quarter-pixel buckets keep resizing smooth without allocating a GDI
        // font handle on every paint.
        var sizeKey = Math.Max(4, (int)Math.Round(emSize * 4f));
        return FontCache.GetOrAdd(sizeKey, key =>
            new Font(SystemIconFamily.Value, key / 4f, FontStyle.Regular, GraphicsUnit.Pixel));
    }

    private static string ResolveSystemIconFamily()
    {
        using var installed = new InstalledFontCollection();
        var available = installed.Families
            .Select(family => family.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return available.Contains(FluentFamily) ? FluentFamily : Mdl2Family;
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
