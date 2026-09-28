using Microsoft.UI.Xaml;

namespace CodexPresence;

/// <summary>Reproducible native screenshots with synthetic data; no daemon or Discord connection.</summary>
internal static class PreviewCapture
{
    public const int ScreenshotCount = 8;

    public static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var dashboard = new MainWindow(AppCoordinator.Version);
        SettingsWindow? settings = null;
        try
        {
            var claude = ClaudeSnapshot();
            dashboard.ApplyAppearance("light");
            dashboard.UpdateSnapshot(claude);
            dashboard.Activate();
            await CaptureAsync(dashboard, directory, "dashboard");
            await NativeHoverChecks.RunAsync(dashboard, directory, "SettingsButton", "PauseButton");
            await NativeHoverChecks.CheckPressAsync(dashboard, directory);

            dashboard.ApplyAppearance("dark");
            dashboard.UpdateSnapshot(CodexSnapshot());
            await CaptureAsync(dashboard, directory, "dashboard-dark");

            dashboard.ApplyAppearance("light");
            claude.PresenceEnabled = false;
            dashboard.UpdateSnapshot(claude);
            await CaptureAsync(dashboard, directory, "paused");
            claude.PresenceEnabled = true;
            dashboard.UpdateSnapshot(claude, "The local status request timed out. Retrying automatically.");
            WindowSizing.ResizeInDips(dashboard, 680, 660);
            await CaptureAsync(dashboard, directory, "offline");
            dashboard.HideWindow();

            settings = new SettingsWindow(new ConfigStore(), new RemoteService(), new PresenceConfig { Appearance = "light" });
            settings.Activate();
            await Task.Delay(700);
            await NativeHoverChecks.RunAsync(settings, directory, "SaveButton", "CancelButton", "PrivacyNavButton");
            await NativeInteractionChecks.RunAsync(settings, directory);
            foreach (var section in new[] { "general", "agents", "privacy", "remote" })
            {
                settings.ShowPage(section);
                await CaptureAsync(settings, directory, section == "remote" ? "settings-ssh" : $"settings-{section}");
            }
            NativeHoverChecks.ThrowIfFailed(directory);
        }
        finally
        {
            settings?.Close();
            dashboard.CloseForExit();
        }
    }

    private static HealthSnapshot ClaudeSnapshot() => new()
    {
        PresenceEnabled = true,
        CodexRunning = true,
        RpcReady = true,
        RpcPublished = true,
        Agent = "claude",
        AgentLabel = "Claude Code",
        AgentRunning = true,
        Project = "codex-presence",
        File = "src/claude-code.js",
        Source = "claude-transcript",
        ActivityName = "Coding with Claude Code",
        StartedAt = DateTimeOffset.Now.AddMinutes(-24).AddSeconds(-18),
        Activity = new PublishedActivity
        {
            Name = "Coding with Claude Code",
            Details = "Project: codex-presence",
            State = "Editing: src/claude-code.js",
        },
        Agents = new AgentsHealth
        {
            Codex = new CodexHealth { Enabled = true, Running = true },
            Claude = new ClaudeHealth { Enabled = true, Installed = true, Active = true, Surface = "desktop", Sessions = 2 },
        },
    };

    private static HealthSnapshot CodexSnapshot() => new()
    {
        PresenceEnabled = true,
        CodexRunning = true,
        RpcReady = true,
        RpcPublished = true,
        Agent = "codex",
        AgentLabel = "Codex",
        AgentRunning = true,
        Project = "codex-presence",
        File = "tray/MainWindow.xaml",
        Source = "desktop-route+session",
        ActivityName = "Coding with Codex",
        StartedAt = DateTimeOffset.Now.AddMinutes(-52).AddSeconds(-7),
        Activity = new PublishedActivity
        {
            Name = "Coding with Codex",
            Details = "Project: codex-presence",
            State = "Editing: tray/MainWindow.xaml",
        },
        Agents = new AgentsHealth
        {
            Codex = new CodexHealth { Enabled = true, Running = true },
            Claude = new ClaudeHealth { Enabled = true, Installed = true },
        },
    };

    private static async Task CaptureAsync(Window window, string directory, string name)
    {
        // Let WinUI finish layout and text rasterization before reading the visual tree.
        await Task.Delay(700);
        await DesktopCapture.SaveAsync(window, Path.Combine(Path.GetFullPath(directory), $"{name}.png"));
    }
}
