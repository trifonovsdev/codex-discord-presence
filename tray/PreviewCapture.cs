using Microsoft.UI.Xaml;

namespace CodexPresence;

/// <summary>Reproducible native screenshots with synthetic data; no daemon or Discord connection.</summary>
internal static class PreviewCapture
{
    public static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var dashboard = new MainWindow(AppCoordinator.Version);
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
            await NativeHoverChecks.RunAsync(dashboard, directory, "SettingsButton", "PauseButton");
            snapshot.PresenceEnabled = false;
            dashboard.UpdateSnapshot(snapshot);
            await CaptureAsync(dashboard, directory, "paused");
            snapshot.PresenceEnabled = true;
            dashboard.UpdateSnapshot(snapshot, "The local status request timed out. Retrying automatically.");
            WindowSizing.ResizeInDips(dashboard, 680, 620);
            await CaptureAsync(dashboard, directory, "offline");
            dashboard.HideWindow();

            settings = new SettingsWindow(new ConfigStore(), new RemoteService(), new PresenceConfig());
            settings.Activate();
            await Task.Delay(700);
            await NativeHoverChecks.RunAsync(settings, directory, "SaveButton", "CancelButton", "PrivacyNavButton");
            await NativeInteractionChecks.RunAsync(settings, directory);
            foreach (var section in new[] { "general", "privacy", "remote" })
            {
                settings.ShowPage(section);
                await CaptureAsync(settings, directory, section == "remote" ? "settings-ssh" : $"settings-{section}");
            }
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
        await DesktopCapture.SaveAsync(window, Path.Combine(Path.GetFullPath(directory), $"{name}.png"));
    }
}
