using System.Diagnostics;
using System.Text.Json;

namespace CodexPresence;

public sealed class DiagnosticsService(DaemonService daemon, ConfigStore configStore, RemoteService remoteService)
{
    private static string SafePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;

        var replacements = new[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%"),
        };

        foreach (var (path, token) in replacements.OrderByDescending(item => item.Item1.Length))
        {
            if (!string.IsNullOrWhiteSpace(path))
                value = value.Replace(path, token, StringComparison.OrdinalIgnoreCase);
        }

        return value;
    }

    public async Task<List<DiagnosticItem>> RunAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<DiagnosticItem>();
        PresenceConfig? config = null;
        try
        {
            config = configStore.Load();
            _ = JsonDocument.Parse(File.ReadAllText(AppPaths.ConfigPath));
            result.Add(new("Configuration", true, SafePath(AppPaths.ConfigPath)));
        }
        catch (Exception error) { result.Add(new("Configuration", false, error.Message)); }

        result.Add(new("Runtime", File.Exists(AppPaths.NodePath) || AppPaths.NodePath == "node.exe", SafePath(AppPaths.NodePath)));
        result.Add(new("Daemon script", File.Exists(AppPaths.DaemonPath), SafePath(AppPaths.DaemonPath)));
        var health = await daemon.HealthAsync(cancellationToken);
        result.Add(new("Local daemon", health?.Ok == true, health is null ? "Not reachable" : $"v{health.Version} on 127.0.0.1:{config?.Port}"));
        result.Add(new("Discord RPC", health?.RpcReady == true, health?.RpcReady == true ? "Connected" : "Open Discord Desktop and enable Activity Privacy"));
        result.Add(new("ChatGPT/Codex", Process.GetProcessesByName(config?.AppProcess ?? "ChatGPT").Length > 0, $"Process: {config?.AppProcess ?? "ChatGPT"}"));

        var hooksOk = false;
        try { hooksOk = File.ReadAllText(AppPaths.HooksPath).Replace("\\\\", "\\").Contains(AppPaths.HookPath, StringComparison.OrdinalIgnoreCase); } catch { }
        result.Add(new("Codex hooks", hooksOk, SafePath(AppPaths.HooksPath)));
        result.Add(new("Windows startup", true, configStore.StartsWithWindows ? "Enabled" : "Disabled (optional)"));

        foreach (var remote in config?.Remote.Hosts ?? [])
        {
            var test = await remoteService.TestAsync(remote, cancellationToken);
            result.Add(new($"SSH: {remote.Name}", test.Ok, test.Output));
        }
        return result;
    }
}
