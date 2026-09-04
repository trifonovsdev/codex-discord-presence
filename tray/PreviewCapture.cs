using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace CodexPresence;

/// <summary>Reproducible native screenshots with synthetic data; no daemon or Discord connection.</summary>
internal static class PreviewCapture
{
    public static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var dashboard = new MainWindow("2.5.0");
        SettingsWindow? settings = null;
        try
        {
            var snapshot = new HealthSnapshot
            {
                PresenceEnabled = true,
                CodexRunning = true,
                RpcReady = true,
                RpcPublished = true,
                Project = "codex-presence",
                File = "tray/MainWindow.xaml",
                Source = "desktop-route+session",
                ActivityName = "Coding with Codex",
                CodexStartedAt = DateTimeOffset.Now.AddMinutes(-24).AddSeconds(-18),
            };
            dashboard.UpdateSnapshot(snapshot);
            dashboard.Activate();
            await CaptureAsync(dashboard, directory, "dashboard");
            snapshot.PresenceEnabled = false;
            dashboard.UpdateSnapshot(snapshot);
            await CaptureAsync(dashboard, directory, "paused");
            dashboard.UpdateSnapshot(null);
            await CaptureAsync(dashboard, directory, "offline");
            dashboard.HideWindow();

            settings = new SettingsWindow(new ConfigStore(), new RemoteService(), new PresenceConfig());
            settings.Activate();
            await CaptureAsync(settings, directory, "settings-general");
            settings.ShowPage("privacy");
            await CaptureAsync(settings, directory, "settings-privacy");
            settings.ShowPage("remote");
            await CaptureAsync(settings, directory, "settings-ssh");
        }
        finally
        {
            settings?.Close();
            dashboard.CloseForExit();
        }
    }

    private static async Task CaptureAsync(Window window, string directory, string name)
    {
        // Let WinUI finish layout and text rasterization before reading the visual tree.
        await Task.Delay(700);
        var root = (FrameworkElement)window.Content;
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(root);
        if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0)
            throw new InvalidOperationException($"The {name} window did not render.");
        var pixels = await bitmap.GetPixelsAsync();
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetFullPath(directory));
        var file = await folder.CreateFileAsync($"{name}.png", CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
        await encoder.FlushAsync();
    }
}
