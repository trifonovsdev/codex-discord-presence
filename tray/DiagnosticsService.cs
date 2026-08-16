using System.Diagnostics;
using System.Text.Json;

namespace CodexPresence;

public sealed class DiagnosticsService(DaemonService daemon, ConfigStore configStore, RemoteService remoteService)
{
    /// <summary>Replaces machine-specific prefixes so a report can be pasted into an issue.</summary>
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
            if (!string.IsNullOrWhiteSpace(path)) value = value.Replace(path, token, StringComparison.OrdinalIgnoreCase);
        }

        return value;
    }

    /// <summary>Counts matching processes without leaking a handle for each one.</summary>
    private static bool IsProcessRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
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
        catch (Exception error)
        {
            result.Add(new("Configuration", false, error.Message));
        }

        result.Add(new("Runtime", File.Exists(AppPaths.NodePath) || AppPaths.NodePath == "node.exe", SafePath(AppPaths.NodePath)));
        result.Add(new("Daemon script", File.Exists(AppPaths.DaemonPath), SafePath(AppPaths.DaemonPath)));

        var health = await daemon.HealthAsync(cancellationToken);
        result.Add(new("Local daemon", health?.Ok == true, health is null ? "Not reachable" : $"v{health.Version} on 127.0.0.1:{config?.Port}"));

        // Surfaces fields the daemon rejected, which otherwise only appear in presence.log.
        if (health?.ConfigWarnings is { Count: > 0 } warnings)
        {
            result.Add(new("Configuration values", false, string.Join("; ", warnings)));
        }

        result.Add(new("Discord RPC", health?.RpcReady == true, health?.RpcReady == true ? "Connected" : "Open Discord Desktop and enable Activity Privacy"));

        var appProcess = config?.AppProcess ?? "ChatGPT";
        result.Add(new("ChatGPT/Codex", IsProcessRunning(appProcess), $"Process: {appProcess}"));

        var hooksOk = false;
        try { hooksOk = File.ReadAllText(AppPaths.HooksPath).Replace("\\\\", "\\").Contains(AppPaths.HookPath, StringComparison.OrdinalIgnoreCase); } catch { }
        var hookDetail = hooksOk
            ? health?.LastHookAt is { } observed
                ? $"Last event received {observed.ToLocalTime():g}"
                : "Registered; no event received since the service started — open a task and review Codex hook permissions if this persists"
            : $"Not registered in {SafePath(AppPaths.HooksPath)} — restart ChatGPT/Codex once after installing";
        result.Add(new(
            "Codex hooks",
            hooksOk,
            hookDetail));

        result.Add(new("Windows startup", true, configStore.StartsWithWindows ? "Enabled" : "Disabled (optional)"));

        foreach (var remote in config?.Remote.Hosts ?? [])
        {
            var test = await remoteService.TestAsync(remote, cancellationToken);
            result.Add(new($"SSH: {remote.Name}", test.Ok, test.Output));
        }

        return result;
    }
}
