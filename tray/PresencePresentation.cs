namespace CodexPresence;

internal enum PresenceTone
{
    Muted,
    Success,
    Warning,
    Danger,
}

/// <summary>How an agent appears in the dashboard switcher.</summary>
internal enum AgentChipState
{
    Disabled,
    Idle,
    Active,
    Owner,
}

internal sealed record AgentChip(AgentChipState State, string ToolTip)
{
    public static AgentChip Unknown(string label) => new(AgentChipState.Idle, $"{label} status is unavailable");
}

/// <summary>
/// Immutable text/state projection shared by the live dashboard and its timer.
/// It deliberately keeps raw paths and private task details out of the Discord preview.
/// </summary>
internal sealed record PresencePresentation(
    string Connection,
    PresenceTone ConnectionTone,
    string ActivityContext,
    string Project,
    string CurrentFile,
    string? CopyPath,
    string Source,
    string Workspace,
    string Session,
    string SharingSummary,
    string PreviewTitle,
    string PreviewPrimary,
    string PreviewSecondary,
    string PreviewElapsed,
    PresenceTone PreviewTone,
    bool ShowPreviewElapsed,
    bool PauseEnabled,
    string PauseText,
    string? WarningTitle,
    string? WarningMessage,
    PresenceTone WarningTone,
    string? PreviewLabelOverride = null)
{
    public const string CodexLabel = "Codex";
    public const string ClaudeLabel = "Claude Code";

    public string PreviewLabel => PreviewLabelOverride ?? (PreviewTone == PresenceTone.Success ? "Published" : "Not published");

    /// <summary>Agent on the card: "codex", "claude", or null while nothing is active.</summary>
    public string? Agent { get; init; }

    /// <summary>Agent whose artwork the preview shows, even while waiting.</summary>
    public string PreviewAgent { get; init; } = "codex";

    public string AgentName { get; init; } = "—";
    public AgentChip CodexChip { get; init; } = AgentChip.Unknown(CodexLabel);
    public AgentChip ClaudeChip { get; init; } = AgentChip.Unknown(ClaudeLabel);

    public static PresencePresentation Create(
        HealthSnapshot? snapshot,
        PrivacyConfig privacy,
        DateTimeOffset now,
        string? connectionError = null,
        DateTimeOffset? lastConfirmedAt = null)
    {
        if (snapshot is null || connectionError is not null)
        {
            var last = snapshot is null ? null : Create(snapshot, privacy, now);
            var connecting = snapshot is null && connectionError is null;
            return new PresencePresentation(
                connecting ? "Connecting to service" : "Status unavailable",
                connecting ? PresenceTone.Muted : PresenceTone.Warning,
                "Checking local service",
                last?.Project ?? "Waiting for service status",
                last?.CurrentFile ?? "Your Discord activity has not been verified yet",
                last?.CopyPath,
                last?.Source ?? "—",
                last?.Workspace ?? "—",
                lastConfirmedAt is { } confirmed ? $"Last seen {confirmed.ToLocalTime():t}" : "Unverified",
                "Visibility configured in Settings",
                last?.PreviewTitle ?? "Discord status unknown",
                last?.PreviewPrimary ?? "Unable to verify activity",
                last?.PreviewSecondary ?? "Discord may still show your last activity.",
                string.Empty,
                PresenceTone.Muted,
                false,
                false,
                "Pause",
                connecting ? null : "Local status connection interrupted",
                connectionError,
                PresenceTone.Warning,
                last is null ? "Status unknown" : "Last confirmed")
            {
                Agent = last?.Agent,
                PreviewAgent = last?.PreviewAgent ?? "codex",
                AgentName = last?.AgentName ?? "—",
                CodexChip = last?.CodexChip ?? AgentChip.Unknown(CodexLabel),
                ClaudeChip = last?.ClaudeChip ?? AgentChip.Unknown(ClaudeLabel),
            };
        }

        var agent = EffectiveAgent(snapshot);
        var running = IsRunning(snapshot);
        var agentLabel = LabelFor(agent);
        var waitingFor = WaitingFor(snapshot);

        var live = snapshot.PresenceEnabled && snapshot.RpcReady && running;
        var published = live && snapshot.RpcPublished;
        var publishFailed = live && !string.IsNullOrWhiteSpace(snapshot.RpcError);

        var (connection, connectionTone) = !snapshot.PresenceEnabled
            ? ("Presence paused", PresenceTone.Muted)
            : !running
                ? ($"Waiting for {waitingFor}", PresenceTone.Muted)
                : publishFailed
                    ? ("Discord rejected update", PresenceTone.Danger)
                    : published
                        ? ("Live on Discord", PresenceTone.Success)
                        : snapshot.RpcReady
                            ? ("Publishing to Discord", PresenceTone.Warning)
                            : ("Waiting for Discord", PresenceTone.Muted);

        var source = running ? FriendlySource(snapshot) : "—";
        var workspace = string.IsNullOrWhiteSpace(snapshot.SelectedRemote)
            ? "Local desktop"
            : snapshot.SelectedRemote!;
        var (session, elapsed) = SessionTiming(snapshot, now);
        var hasProject = running && !string.IsNullOrWhiteSpace(snapshot.Project);
        var project = hasProject
            ? snapshot.Project!
            : running ? $"Working in {agentLabel}" : $"Waiting for {waitingFor}";
        var file = hasProject
            ? string.IsNullOrWhiteSpace(snapshot.File) ? "No edited file yet" : snapshot.File!
            : running ? "No detectable workspace for this session" : StartHint(snapshot);

        var (warningTitle, warningMessage, warningTone) = Warning(snapshot);
        var preview = Preview(snapshot, privacy, agent, published, publishFailed, running, waitingFor, elapsed);

        return new PresencePresentation(
            connection,
            connectionTone,
            $"{(running ? agentLabel : "Idle")}  ·  {workspace}",
            project,
            file,
            hasProject && !string.IsNullOrWhiteSpace(snapshot.File) ? snapshot.File : null,
            source,
            running ? workspace : "—",
            session,
            BuildSharingSummary(privacy, published),
            preview.Title,
            preview.Primary,
            preview.Secondary,
            elapsed,
            preview.Tone,
            preview.ShowElapsed,
            true,
            snapshot.PresenceEnabled ? "Pause presence" : "Resume presence",
            warningTitle,
            warningMessage,
            warningTone)
        {
            Agent = running ? agent : null,
            PreviewAgent = agent ?? PreferredIdleAgent(snapshot),
            AgentName = running ? agentLabel : "—",
            CodexChip = Chip("codex", snapshot),
            ClaudeChip = Chip("claude", snapshot),
        };
    }

    /// <summary>Agent that owns the card. Older services only know about Codex.</summary>
    private static string? EffectiveAgent(HealthSnapshot snapshot) =>
        snapshot.Agents is null
            ? snapshot.CodexRunning ? "codex" : null
            : snapshot.Agent is "codex" or "claude" ? snapshot.Agent : null;

    private static bool IsRunning(HealthSnapshot snapshot) =>
        snapshot.Agents is null ? snapshot.CodexRunning : snapshot.AgentRunning && EffectiveAgent(snapshot) is not null;

    private static DateTimeOffset? StartedAt(HealthSnapshot snapshot) =>
        snapshot.Agents is null ? snapshot.CodexStartedAt : snapshot.StartedAt;

    internal static string LabelFor(string? agent) => agent == "claude" ? ClaudeLabel : CodexLabel;

    private static string WaitingFor(HealthSnapshot snapshot)
    {
        if (snapshot.Agents is null) return CodexLabel;
        var codex = snapshot.Agents.Codex.Enabled;
        var claude = snapshot.Agents.Claude.Enabled;
        return codex && claude ? "an agent" : claude ? ClaudeLabel : codex ? CodexLabel : "an agent";
    }

    private static string StartHint(HealthSnapshot snapshot)
    {
        if (snapshot.Agents is null) return "Open a task to start sharing activity";
        var codex = snapshot.Agents.Codex.Enabled;
        var claude = snapshot.Agents.Claude.Enabled;
        return codex && claude
            ? "Open a Codex task or start Claude Code to share activity"
            : claude
                ? "Start a Claude Code session to share activity"
                : codex
                    ? "Open a task in Codex to share activity"
                    : "Turn on an agent in Settings to share activity";
    }

    private static string PreferredIdleAgent(HealthSnapshot snapshot) =>
        snapshot.Agents is { } agents && (agents.Preferred == "claude" || (!agents.Codex.Enabled && agents.Claude.Enabled))
            ? "claude"
            : "codex";

    private static AgentChip Chip(string agent, HealthSnapshot snapshot)
    {
        var label = LabelFor(agent);
        if (snapshot.Agents is null)
        {
            return agent == "codex"
                ? new(snapshot.CodexRunning ? AgentChipState.Owner : AgentChipState.Idle, snapshot.CodexRunning ? "Codex is open" : "Codex is not running")
                : new(AgentChipState.Disabled, "Restart the service to follow Claude Code");
        }

        var owner = snapshot.PresenceEnabled && snapshot.AgentRunning && snapshot.Agent == agent;
        if (agent == "codex")
        {
            var codex = snapshot.Agents.Codex;
            if (!codex.Enabled) return new(AgentChipState.Disabled, "Codex is turned off in Settings");
            return owner
                ? new(AgentChipState.Owner, "Codex is on your Discord card")
                : new(codex.Running ? AgentChipState.Active : AgentChipState.Idle, codex.Running ? "Codex is open" : "Codex is not running");
        }

        var claude = snapshot.Agents.Claude;
        if (!claude.Enabled) return new(AgentChipState.Disabled, "Claude Code is turned off in Settings");
        var sessions = claude.Sessions == 1 ? "1 active session" : $"{claude.Sessions} active sessions";
        var where = string.IsNullOrWhiteSpace(claude.Workspace) ? string.Empty : $" on {claude.Workspace}";
        if (owner) return new(AgentChipState.Owner, $"Claude Code is on your Discord card · {sessions}{where}");
        if (claude.Active) return new(AgentChipState.Active, $"Claude Code is active · {sessions}{where}");
        var remote = claude.Remote.Count > 0;
        return new(
            AgentChipState.Idle,
            claude.Installed || remote ? "Claude Code is idle" : "Claude Code was not found on this PC");
    }

    private static (string Title, string Primary, string Secondary, PresenceTone Tone, bool ShowElapsed) Preview(
        HealthSnapshot snapshot,
        PrivacyConfig privacy,
        string? agent,
        bool published,
        bool publishFailed,
        bool running,
        string waitingFor,
        string elapsed)
    {
        if (!published)
        {
            if (publishFailed)
                return ("Discord rejected update", "Activity was not published", snapshot.RpcError!, PresenceTone.Danger, false);
            if (!snapshot.PresenceEnabled)
                return ("Presence paused", "Nothing is shared with Discord", "Resume when you are ready to publish again.", PresenceTone.Muted, false);
            if (!running)
                return ($"Waiting for {waitingFor}", "Nothing is published yet", StartHint(snapshot) + ".", PresenceTone.Muted, false);
            return ("Publishing…", "Waiting for Discord", "The current presence update has not been acknowledged yet.", PresenceTone.Warning, false);
        }

        var showElapsed = privacy.ShowTimer && StartedAt(snapshot) is not null && elapsed.Length > 0;
        if (snapshot.Activity is { } activity && !string.IsNullOrWhiteSpace(activity.Name))
        {
            // The service reports exactly what Discord received.
            return (activity.Name!, activity.Details ?? string.Empty, activity.State ?? string.Empty, PresenceTone.Success, showElapsed);
        }

        var label = LabelFor(agent);
        var russian = string.Equals(snapshot.Language, "ru", StringComparison.OrdinalIgnoreCase);
        var showTask = privacy.ShowTaskTitle && snapshot.TaskTitleShared && !string.IsNullOrWhiteSpace(snapshot.Task);
        string primary;
        if (privacy.ShowProject && !string.IsNullOrWhiteSpace(snapshot.Project))
            primary = russian ? $"Проект: {snapshot.Project}" : $"Project: {snapshot.Project}";
        else if (showTask)
            primary = russian ? $"Задача: {snapshot.Task}" : $"Task: {snapshot.Task}";
        else
            primary = russian ? $"Работает в {label}" : $"Working in {label}";

        string secondary;
        if (!privacy.ShowFile)
            secondary = russian ? "Работает приватно" : "Working privately";
        else if (string.IsNullOrWhiteSpace(snapshot.File))
            secondary = russian ? $"Активная сессия {label}" : $"Active {label} session";
        else
        {
            var path = string.Equals(privacy.FileMode, "name", StringComparison.OrdinalIgnoreCase)
                ? FileName(snapshot.File!)
                : snapshot.File!;
            secondary = russian ? $"Файл: {path}" : $"Editing: {path}";
        }

        var rawActivityName = snapshot.ActivityName?.Trim();
        var activityName = rawActivityName is { Length: >= 2 }
            ? rawActivityName[..Math.Min(rawActivityName.Length, 128)]
            : $"Coding with {label}";

        return (activityName, primary, secondary, PresenceTone.Success, showElapsed);
    }

    private static (string? Title, string? Message, PresenceTone Tone) Warning(HealthSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.RpcError))
            return ("Discord could not publish this update", snapshot.RpcError, PresenceTone.Danger);
        if (!string.IsNullOrWhiteSpace(snapshot.LastRemoteError))
            return ("SSH workspace needs attention", snapshot.LastRemoteError, PresenceTone.Warning);
        if (snapshot.Agents?.Claude.Remote.FirstOrDefault(static remote => remote.Outdated) is { } outdated)
            return ("Update the SSH helper", $"{outdated.Name}: {outdated.Error}", PresenceTone.Warning);
        if (snapshot.ConfigWarnings.FirstOrDefault(static warning => !string.IsNullOrWhiteSpace(warning)) is { } warning)
            return ("Configuration warning", warning, PresenceTone.Warning);
        return (null, null, PresenceTone.Muted);
    }

    private static string BuildSharingSummary(PrivacyConfig privacy, bool published)
    {
        var shared = new List<string>(4);
        if (privacy.ShowProject) shared.Add("project");
        if (privacy.ShowTaskTitle) shared.Add("task");
        if (privacy.ShowFile) shared.Add("file");
        if (privacy.ShowTimer) shared.Add("timer");
        var fields = shared.Count == 0 ? "app name only" : string.Join(" · ", shared);
        return $"{(published ? "Sharing" : "Configured:")} {fields}";
    }

    private static string FriendlySource(HealthSnapshot snapshot) => snapshot.Source switch
    {
        "desktop-route+remote-session" => "Remote task",
        "desktop-route+session" => "Selected task",
        "desktop-route" => "Desktop route",
        "hook" or "claude-hook" => "Live hook",
        "claude-remote" => "Remote session",
        "claude-transcript" => snapshot.Agents?.Claude.Surface switch
        {
            "desktop" => "Claude Desktop",
            "ide" => "IDE session",
            "terminal" => "Terminal session",
            "sdk" => "SDK session",
            _ => "Session log",
        },
        _ => "Session monitor",
    };

    private static string FileName(string path)
    {
        var normalized = path.Replace('\\', '/');
        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash >= 0 && lastSlash < normalized.Length - 1
            ? normalized[(lastSlash + 1)..]
            : normalized;
    }

    internal static (string Session, string Elapsed) SessionTiming(HealthSnapshot? snapshot, DateTimeOffset now)
    {
        if (snapshot is null) return ("Unavailable", "No active session");
        var startedAt = StartedAt(snapshot);
        var elapsed = FormatElapsed(startedAt, now);
        var session = startedAt is null
            ? IsRunning(snapshot) ? "Active now" : "No active session"
            : $"Elapsed {elapsed}";
        return (session, elapsed);
    }

    private static string FormatElapsed(DateTimeOffset? startedAt, DateTimeOffset now)
    {
        if (startedAt is null) return "No active session";
        var value = now - startedAt.Value;
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        return value.TotalHours >= 24
            ? $"{(int)value.TotalDays}d {value:hh\\:mm\\:ss}"
            : value.ToString("hh\\:mm\\:ss");
    }
}
