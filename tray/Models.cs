using System.Text.Json.Serialization;

namespace CodexPresence;

public sealed class PresenceConfig
{
    [JsonPropertyName("clientId")] public string ClientId { get; set; } = "1526968377048956938";
    [JsonPropertyName("port")] public int Port { get; set; } = 37642;
    [JsonPropertyName("language")] public string Language { get; set; } = "en";
    [JsonPropertyName("activityName")] public string ActivityName { get; set; } = "Coding with Codex";
    [JsonPropertyName("largeImageKey")] public string LargeImageKey { get; set; } = "codex";
    [JsonPropertyName("largeImageText")] public string LargeImageText { get; set; } = "OpenAI Codex";
    [JsonPropertyName("appProcess")] public string AppProcess { get; set; } = "ChatGPT";
    [JsonPropertyName("presenceEnabled")] public bool PresenceEnabled { get; set; } = true;
    [JsonPropertyName("appearance")] public string Appearance { get; set; } = "system";
    [JsonPropertyName("agents")] public AgentsConfig Agents { get; set; } = new();
    [JsonPropertyName("privacy")] public PrivacyConfig Privacy { get; set; } = new();
    [JsonPropertyName("remote")] public RemoteConfig Remote { get; set; } = new();
    [JsonPropertyName("updates")] public UpdateConfig Updates { get; set; } = new();
}

public sealed class AgentsConfig
{
    [JsonPropertyName("preferred")] public string Preferred { get; set; } = "auto";
    [JsonPropertyName("codex")] public CodexAgentConfig Codex { get; set; } = new();
    [JsonPropertyName("claude")] public ClaudeAgentConfig Claude { get; set; } = new();
}

public sealed class CodexAgentConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}

public sealed class ClaudeAgentConfig
{
    public const string DefaultActivityName = "Coding with Claude Code";
    public const string DefaultImage = "https://raw.githubusercontent.com/trifonovsdev/codex-discord-presence/main/assets/discord/claude-code.png";

    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("activityName")] public string ActivityName { get; set; } = DefaultActivityName;
    [JsonPropertyName("largeImageKey")] public string LargeImageKey { get; set; } = DefaultImage;
    [JsonPropertyName("largeImageText")] public string LargeImageText { get; set; } = "Claude Code";
    [JsonPropertyName("idleMinutes")] public int IdleMinutes { get; set; } = 10;
    [JsonPropertyName("hooks")] public bool Hooks { get; set; } = true;
    [JsonPropertyName("remote")] public bool Remote { get; set; } = true;
}

public sealed class PrivacyConfig
{
    [JsonPropertyName("preset")] public string Preset { get; set; } = "standard";
    [JsonPropertyName("showProject")] public bool ShowProject { get; set; } = true;
    [JsonPropertyName("showTaskTitle")] public bool ShowTaskTitle { get; set; }
    [JsonPropertyName("showFile")] public bool ShowFile { get; set; } = true;
    [JsonPropertyName("showTimer")] public bool ShowTimer { get; set; } = true;
    [JsonPropertyName("fileMode")] public string FileMode { get; set; } = "relative";
}

public sealed class RemoteConfig
{
    [JsonPropertyName("host")] public string Host { get; set; } = "";
    [JsonPropertyName("hosts")] public List<RemoteHostConfig> Hosts { get; set; } = [];
    [JsonPropertyName("monitorPath")] public string MonitorPath { get; set; } = "~/.local/share/CodexDiscordPresence/remote-monitor.py";
    [JsonPropertyName("pollIntervalMs")] public int PollIntervalMs { get; set; } = 7000;
}

public sealed class RemoteHostConfig
{
    [JsonPropertyName("name")] public string Name { get; set; } = "Remote";
    [JsonPropertyName("host")] public string Host { get; set; } = "";
    [JsonPropertyName("roots")] public List<string> Roots { get; set; } = [];
    [JsonPropertyName("monitorPath")] public string MonitorPath { get; set; } = "~/.local/share/CodexDiscordPresence/remote-monitor.py";
}

public sealed class UpdateConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("repository")] public string Repository { get; set; } = "trifonovsdev/codex-discord-presence";
    [JsonPropertyName("checkIntervalHours")] public int CheckIntervalHours { get; set; } = 24;
}

public sealed class HealthSnapshot
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("activityName")] public string? ActivityName { get; set; }
    [JsonPropertyName("rpcReady")] public bool RpcReady { get; set; }
    [JsonPropertyName("rpcPublished")] public bool RpcPublished { get; set; }
    [JsonPropertyName("rpcError")] public string? RpcError { get; set; }
    [JsonPropertyName("rpcTransport")] public string? RpcTransport { get; set; }
    [JsonPropertyName("agent")] public string? Agent { get; set; }
    [JsonPropertyName("agentLabel")] public string? AgentLabel { get; set; }
    [JsonPropertyName("agentRunning")] public bool AgentRunning { get; set; }
    [JsonPropertyName("startedAt")] public DateTimeOffset? StartedAt { get; set; }
    [JsonPropertyName("activity")] public PublishedActivity? Activity { get; set; }
    [JsonPropertyName("agents")] public AgentsHealth? Agents { get; set; }
    [JsonPropertyName("codexRunning")] public bool CodexRunning { get; set; }
    [JsonPropertyName("configWarnings")] public List<string> ConfigWarnings { get; set; } = [];
    [JsonPropertyName("presenceEnabled")] public bool PresenceEnabled { get; set; }
    [JsonPropertyName("project")] public string? Project { get; set; }
    [JsonPropertyName("task")] public string? Task { get; set; }
    [JsonPropertyName("taskTitleShared")] public bool TaskTitleShared { get; set; }
    [JsonPropertyName("file")] public string? File { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("codexStartedAt")] public DateTimeOffset? CodexStartedAt { get; set; }
    [JsonPropertyName("lastRpcAck")] public DateTimeOffset? LastRpcAck { get; set; }
    [JsonPropertyName("lastHookAt")] public DateTimeOffset? LastHookAt { get; set; }
    [JsonPropertyName("lastRemoteError")] public string? LastRemoteError { get; set; }
    [JsonPropertyName("selectedRemote")] public string? SelectedRemote { get; set; }
}

public sealed class PublishedActivity
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("details")] public string? Details { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("largeText")] public string? LargeText { get; set; }
}

public sealed class AgentsHealth
{
    [JsonPropertyName("preferred")] public string? Preferred { get; set; }
    [JsonPropertyName("codex")] public CodexHealth Codex { get; set; } = new();
    [JsonPropertyName("claude")] public ClaudeHealth Claude { get; set; } = new();
}

public sealed class CodexHealth
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("running")] public bool Running { get; set; }
    [JsonPropertyName("project")] public string? Project { get; set; }
}

public sealed class ClaudeHealth
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("installed")] public bool Installed { get; set; }
    [JsonPropertyName("active")] public bool Active { get; set; }
    [JsonPropertyName("busy")] public bool Busy { get; set; }
    [JsonPropertyName("project")] public string? Project { get; set; }
    [JsonPropertyName("workspace")] public string? Workspace { get; set; }
    [JsonPropertyName("surface")] public string? Surface { get; set; }
    [JsonPropertyName("sessions")] public int Sessions { get; set; }
    [JsonPropertyName("lastHookAt")] public DateTimeOffset? LastHookAt { get; set; }
    [JsonPropertyName("hooks")] public bool Hooks { get; set; }
    [JsonPropertyName("remote")] public List<ClaudeRemoteHealth> Remote { get; set; } = [];
}

public sealed class ClaudeRemoteHealth
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("active")] public bool Active { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("outdated")] public bool Outdated { get; set; }
}

public sealed record DiagnosticItem(string Name, bool? Passed, string Detail);

public sealed record ReleaseInfo(Version Version, string Name, string PageUrl, string? InstallerUrl, string? ChecksumsUrl);
