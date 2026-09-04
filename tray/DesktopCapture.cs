using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.Graphics.Imaging;
using Windows.Storage;
using WinRT.Interop;

namespace CodexPresence;

/// <summary>Captures compositor output for the opt-in native preview; never used by normal app execution.</summary>
internal static class DesktopCapture
{
    internal sealed record Frame(int Width, int Height, byte[] Pixels);

    public static Task SaveAsync(Window window, string path) => SaveAsync(Capture(window), path);

    public static Frame Capture(Window window, bool includeCursor = false)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        if (!GetClientRect(hwnd, out var bounds)) throw new InvalidOperationException("Cannot read preview bounds.");
        var origin = new Point();
        if (!ClientToScreen(hwnd, ref origin)) throw new InvalidOperationException("Cannot locate preview on the desktop.");
        var width = bounds.Right;
        var height = bounds.Bottom;
        var screen = GetDC(0);
        var target = CreateCompatibleDC(screen);
        var info = new BitmapInfo { Size = (uint)Marshal.SizeOf<BitmapInfo>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
        var bitmap = CreateDIBSection(screen, ref info, 0, out var pixels, 0, 0);
        var previous = SelectObject(target, bitmap);
        var data = new byte[checked(width * height * 4)];
        try
        {
            if (bitmap == 0 || !BitBlt(target, 0, 0, width, height, screen, origin.X, origin.Y, 0x00CC0020))
                throw new InvalidOperationException("Cannot capture native compositor output.");
            if (includeCursor) DrawCursor(target, origin);
            Marshal.Copy(pixels, data, 0, data.Length);
        }
        finally
        {
            SelectObject(target, previous);
            DeleteObject(bitmap);
            DeleteDC(target);
            ReleaseDC(0, screen);
        }
        return new Frame(width, height, data);
    }

    public static async Task SaveAsync(Frame frame, string path)
    {
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
        var file = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)frame.Width, (uint)frame.Height, 96, 96, frame.Pixels);
        await encoder.FlushAsync();
    }

    public static async Task SaveSequenceAsync(string directory, IReadOnlyList<Frame> frames, IReadOnlyList<double> seconds)
    {
        Directory.CreateDirectory(directory);
        var concat = new List<string>();
        for (var i = 0; i < frames.Count; i++)
        {
            await SaveAsync(frames[i], Path.Combine(directory, $"frame-{i:D4}.png"));
            concat.Add($"file 'frame-{i:D4}.png'");
            var duration = i + 1 < seconds.Count ? seconds[i + 1] - seconds[i] : 0.4;
            concat.Add("duration " + duration.ToString("F6", CultureInfo.InvariantCulture));
        }
        concat.Add($"file 'frame-{frames.Count - 1:D4}.png'");
        File.WriteAllLines(Path.Combine(directory, "frames.txt"), concat);
    }

    private static void DrawCursor(nint target, Point origin)
    {
        var cursor = new CursorInfo { Size = (uint)Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref cursor) || cursor.Flags != 1 || !GetIconInfo(cursor.Handle, out var icon)) return;
        try
        {
            DrawIconEx(target, cursor.Position.X - origin.X - (int)icon.HotspotX,
                cursor.Position.Y - origin.Y - (int)icon.HotspotY, cursor.Handle, 0, 0, 0, 0, 3);
        }
        finally
        {
            if (icon.Mask != 0) DeleteObject(icon.Mask);
            if (icon.Color != 0) DeleteObject(icon.Color);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct CursorInfo { public uint Size, Flags; public nint Handle; public Point Position; }
    [StructLayout(LayoutKind.Sequential)] private struct IconInfo { public int IsIcon; public uint HotspotX, HotspotY; public nint Mask, Color; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo cursor);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(nint icon, out IconInfo info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
}
