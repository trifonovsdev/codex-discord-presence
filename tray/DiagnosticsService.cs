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
        result.Add(new("Discord Social SDK", File.Exists(AppPaths.SocialSdkPath), SafePath(AppPaths.SocialSdkPath)));

        var health = await daemon.HealthAsync(cancellationToken);
        result.Add(new("Local daemon", health?.Ok == true, health is null ? daemon.LastHealthError ?? "Status unavailable" : $"v{health.Version} on 127.0.0.1:{config?.Port}"));

        // Surfaces fields the daemon rejected, which otherwise only appear in presence.log.
        if (health?.ConfigWarnings is { Count: > 0 } warnings)
        {
            result.Add(new("Configuration values", false, string.Join("; ", warnings)));
        }

        var publisher = health?.RpcTransport == "social-sdk" ? "Social SDK" : "legacy RPC fallback";
        result.Add(new(
            "Discord publisher",
            health?.RpcReady,
            health is null ? "Not checked: local status is unavailable. Discord may still be publishing."
            : health.RpcReady
                ? $"Connected through {publisher}"
                : health?.RpcError ?? "Open Discord Desktop and enable Activity Privacy"));

        var codexEnabled = config?.Agents?.Codex?.Enabled ?? true;
        var appProcess = config?.AppProcess ?? "ChatGPT";
        result.Add(codexEnabled
            ? new DiagnosticItem("ChatGPT/Codex", IsProcessRunning(appProcess), $"Process: {appProcess}")
            : new DiagnosticItem("ChatGPT/Codex", null, "Turned off in Settings → Agents"));

        var hooksOk = false;
        try { hooksOk = File.ReadAllText(AppPaths.HooksPath).Replace("\\\\", "\\").Contains(AppPaths.HookPath, StringComparison.OrdinalIgnoreCase); } catch { }
        var hookDetail = hooksOk
            ? health is null ? "Registered; event delivery could not be checked while local status is unavailable."
            : health.LastHookAt is { } observed
                ? $"Last event received {observed.ToLocalTime():g}"
                : "Registered; no event received since the service started — open a task and review Codex hook permissions if this persists"
            : $"Not registered in {SafePath(AppPaths.HooksPath)} — restart ChatGPT/Codex once after installing";
        result.Add(codexEnabled
            ? new DiagnosticItem("Codex hooks", hooksOk, hookDetail)
            : new DiagnosticItem("Codex hooks", null, "Not needed while Codex is turned off"));

        AddClaudeChecks(result, config, health);

        result.Add(new("Windows startup", true, configStore.StartsWithWindows ? "Enabled" : "Disabled (optional)"));

        foreach (var remote in config?.Remote.Hosts ?? [])
        {
            var test = await remoteService.TestAsync(remote, cancellationToken);
            result.Add(new($"SSH: {remote.Name}", test.Ok, test.Output));
        }

        return result;
    }

    private static void AddClaudeChecks(List<DiagnosticItem> result, PresenceConfig? config, HealthSnapshot? health)
    {
        var claude = config?.Agents?.Claude ?? new ClaudeAgentConfig();
        if (!claude.Enabled)
        {
            result.Add(new("Claude Code", null, "Turned off in Settings → Agents"));
            return;
        }

        var state = health?.Agents?.Claude;
        var remoteHosts = config?.Remote.Hosts.Count ?? 0;
        var profile = SafePath(ClaudeCodeHooks.Home);
        string detail;
        if (state is null)
            detail = health is null ? "Not checked: local status is unavailable" : "Restart the service to follow Claude Code";
        else if (state.Active)
            detail = $"Active{(string.IsNullOrWhiteSpace(state.Project) ? string.Empty : $" in {state.Project}")}" +
                     $"{(string.IsNullOrWhiteSpace(state.Workspace) ? string.Empty : $" on {state.Workspace}")} · " +
                     $"{(state.Sessions == 1 ? "1 session" : $"{state.Sessions} sessions")}";
        else if (ClaudeCodeHooks.ClaudeInstalled)
            detail = $"Found {profile}; no session active in the last {claude.IdleMinutes} min";
        else
            detail = remoteHosts > 0 ? "Not installed on this PC; SSH workspaces are checked" : $"Not found at {profile}";
        bool? found = state is null ? null : ClaudeCodeHooks.ClaudeInstalled || remoteHosts > 0 || state.Active;
        result.Add(new DiagnosticItem("Claude Code", found, detail));

        if (!claude.Hooks)
        {
            result.Add(new("Claude Code hooks", null, "Off: sessions are read from transcripts every few seconds"));
        }
        else if (!ClaudeCodeHooks.ClaudeInstalled)
        {
            result.Add(new("Claude Code hooks", null, "Not needed until Claude Code is installed on this PC"));
        }
        else
        {
            var registered = ClaudeCodeHooks.IsRegistered();
            result.Add(new(
                "Claude Code hooks",
                registered,
                registered
                    ? state?.LastHookAt is { } seen ? $"Last event received {seen.ToLocalTime():g}" : "Registered; events arrive with the next prompt"
                    : $"Not registered in {SafePath(ClaudeCodeHooks.SettingsPath)} — save Settings to add them"));
        }

        if (!claude.Remote) return;
        foreach (var remote in state?.Remote ?? [])
        {
            if (string.IsNullOrWhiteSpace(remote.Error)) continue;
            result.Add(new($"Claude Code: {remote.Name}", false, remote.Error!));
        }
    }
}
