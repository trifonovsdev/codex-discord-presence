namespace CodexPresence;

internal enum PresenceTone
{
    Muted,
    Success,
    Warning,
    Danger,
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
    public string PreviewLabel => PreviewLabelOverride ?? (PreviewTone == PresenceTone.Success ? "Published" : "Not published");

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
            return new(
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
                last is null ? "Status unknown" : "Last confirmed");
        }

        var live = snapshot.PresenceEnabled && snapshot.RpcReady && snapshot.CodexRunning;
        var published = live && snapshot.RpcPublished;
        var publishFailed = live && !string.IsNullOrWhiteSpace(snapshot.RpcError);

        var (connection, connectionTone) = !snapshot.PresenceEnabled
            ? ("Presence paused", PresenceTone.Muted)
            : !snapshot.CodexRunning
                ? ("Waiting for Codex", PresenceTone.Muted)
                : publishFailed
                    ? ("Discord rejected update", PresenceTone.Danger)
                    : published
                        ? ("Live on Discord", PresenceTone.Success)
                        : snapshot.RpcReady
                            ? ("Publishing to Discord", PresenceTone.Warning)
                            : ("Waiting for Discord", PresenceTone.Muted);

        var source = FriendlySource(snapshot.Source);
        var workspace = string.IsNullOrWhiteSpace(snapshot.SelectedRemote)
            ? "Local desktop"
            : snapshot.SelectedRemote!;
        var (session, elapsed) = SessionTiming(snapshot, now);
        var project = !string.IsNullOrWhiteSpace(snapshot.Project)
            ? snapshot.Project!
            : snapshot.CodexRunning ? "Working in Codex" : "Waiting for Codex";
        var file = !string.IsNullOrWhiteSpace(snapshot.Project)
            ? string.IsNullOrWhiteSpace(snapshot.File) ? "No edited file yet" : snapshot.File!
            : snapshot.CodexRunning ? "No detectable workspace for this task" : "Open a task to start sharing activity";

        var (warningTitle, warningMessage, warningTone) = Warning(snapshot);
        var preview = Preview(snapshot, privacy, published, publishFailed, elapsed);

        return new(
            connection,
            connectionTone,
            $"{source}  ·  {workspace}",
            project,
            file,
            string.IsNullOrWhiteSpace(snapshot.File) ? null : snapshot.File,
            source,
            workspace,
            session,
            BuildSharingSummary(privacy),
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
            warningTone);
    }

    private static (string Title, string Primary, string Secondary, PresenceTone Tone, bool ShowElapsed) Preview(
        HealthSnapshot snapshot,
        PrivacyConfig privacy,
        bool published,
        bool publishFailed,
        string elapsed)
    {
        if (!published)
        {
            if (publishFailed)
                return ("Discord rejected update", "Activity was not published", snapshot.RpcError!, PresenceTone.Danger, false);
            if (!snapshot.PresenceEnabled)
                return ("Presence paused", "Nothing is shared with Discord", "Resume when you are ready to publish again.", PresenceTone.Muted, false);
            if (!snapshot.CodexRunning)
                return ("Waiting for Codex", "Nothing is published yet", "Open a task in Codex to start a session.", PresenceTone.Muted, false);
            return ("Publishing…", "Waiting for Discord", "The current presence update has not been acknowledged yet.", PresenceTone.Warning, false);
        }

        var russian = string.Equals(snapshot.Language, "ru", StringComparison.OrdinalIgnoreCase);
        var showTask = privacy.ShowTaskTitle && snapshot.TaskTitleShared && !string.IsNullOrWhiteSpace(snapshot.Task);
        string primary;
        if (privacy.ShowProject && !string.IsNullOrWhiteSpace(snapshot.Project))
            primary = russian ? $"Проект: {snapshot.Project}" : $"Project: {snapshot.Project}";
        else if (showTask)
            primary = russian ? $"Задача: {snapshot.Task}" : $"Task: {snapshot.Task}";
        else
            primary = russian ? "Работает в Codex" : "Working in Codex";

        string secondary;
        if (!privacy.ShowFile)
            secondary = russian ? "Работает приватно" : "Working privately";
        else if (string.IsNullOrWhiteSpace(snapshot.File))
            secondary = russian ? "Активная сессия Codex" : "Active Codex session";
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
            : "Coding with Codex";

        return (activityName, primary, secondary, PresenceTone.Success,
            privacy.ShowTimer && snapshot.CodexStartedAt is not null && elapsed.Length > 0);
    }

    private static (string? Title, string? Message, PresenceTone Tone) Warning(HealthSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.RpcError))
            return ("Discord could not publish this update", snapshot.RpcError, PresenceTone.Danger);
        if (!string.IsNullOrWhiteSpace(snapshot.LastRemoteError))
            return ("SSH workspace needs attention", snapshot.LastRemoteError, PresenceTone.Warning);
        if (snapshot.ConfigWarnings.FirstOrDefault(static warning => !string.IsNullOrWhiteSpace(warning)) is { } warning)
            return ("Configuration warning", warning, PresenceTone.Warning);
        return (null, null, PresenceTone.Muted);
    }

    private static string BuildSharingSummary(PrivacyConfig privacy)
    {
        var shared = new List<string>(4);
        if (privacy.ShowProject) shared.Add("project");
        if (privacy.ShowTaskTitle) shared.Add("task");
        if (privacy.ShowFile) shared.Add("file");
        if (privacy.ShowTimer) shared.Add("timer");
        return shared.Count == 0 ? "Sharing nothing" : $"Sharing {string.Join(" · ", shared)}";
    }

    private static string FriendlySource(string? value) => value switch
    {
        "desktop-route+remote-session" => "Remote task",
        "desktop-route+session" => "Selected task",
        "desktop-route" => "Desktop route",
        "hook" => "Live hook",
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
        var elapsed = FormatElapsed(snapshot.CodexStartedAt, now);
        var session = snapshot.CodexStartedAt is null
            ? snapshot.CodexRunning ? "Active now" : "No active task"
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
