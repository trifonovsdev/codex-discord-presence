using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace CodexPresence;

/// <summary>Converts logical WinUI dimensions to the physical pixels expected by AppWindow.</summary>
internal static class WindowSizing
{
    private const double DefaultDpi = 96d;

    public static void ResizeInDips(Window window, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        var windowHandle = WindowNative.GetWindowHandle(window);
        var dpi = windowHandle == 0 ? 0u : GetDpiForWindow(windowHandle);
        var scale = (dpi == 0 ? DefaultDpi : dpi) / DefaultDpi;
        window.AppWindow.Resize(new SizeInt32(DipsToPixels(width, scale), DipsToPixels(height, scale)));
    }

    internal static int DipsToPixels(int dips, double scale) =>
        Math.Max(1, checked((int)Math.Ceiling(dips * scale)));

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(nint window);
}
