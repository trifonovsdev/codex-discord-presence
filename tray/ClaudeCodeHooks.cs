using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexPresence;

/// <summary>
/// Registers lightweight Codex Presence hooks in Claude Code's user settings.
/// Only entries carrying <see cref="Marker"/> are ever added or removed; every
/// other setting and hook in the file is preserved as written.
/// </summary>
public static class ClaudeCodeHooks
{
    public const string Marker = "--agent claude";

    // Edits and prompts only: Claude Code waits for hooks, so nothing runs per read or shell command.
    private static readonly (string Event, string? Matcher)[] Events =
    [
        ("SessionStart", null),
        ("UserPromptSubmit", null),
        ("PostToolUse", "Edit|MultiEdit|Write|NotebookEdit"),
        ("SessionEnd", null),
    ];

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static string Home => Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    public static string SettingsPath => Path.Combine(Home, "settings.json");

    /// <summary>True when this Windows account has a Claude Code profile.</summary>
    public static bool ClaudeInstalled => Directory.Exists(Home);

    /// <summary>Forward slashes work in both cmd.exe and the Git Bash shell Claude Code uses on Windows.</summary>
    public static string Command =>
        $"\"{AppPaths.NodePath.Replace('\\', '/')}\" \"{AppPaths.HookPath.Replace('\\', '/')}\" {Marker}";

    public static bool IsRegistered()
    {
        try
        {
            if (Read() is not { } root || root["hooks"] is not JsonObject hooks) return false;
            return Events.All(item => hooks[item.Event] is JsonArray groups &&
                groups.OfType<JsonObject>().Any(group => Commands(group).Contains(Command, StringComparer.OrdinalIgnoreCase)));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Adds or refreshes the registrations. Returns true when settings.json changed.</summary>
    public static bool Install()
    {
        if (!ClaudeInstalled) return false;
        var root = Read() ?? [];
        var before = root.ToJsonString();
        RemoveOwn(root);

        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = [];
            root["hooks"] = hooks;
        }

        foreach (var (name, matcher) in Events)
        {
            if (hooks[name] is not JsonArray groups)
            {
                groups = [];
                hooks[name] = groups;
            }

            var group = new JsonObject();
            if (matcher is not null) group["matcher"] = matcher;
            group["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = Command,
                ["timeout"] = 5,
            });
            groups.Add(group);
        }

        return WriteIfChanged(root, before);
    }

    /// <summary>Removes only Codex Presence registrations. Returns true when settings.json changed.</summary>
    public static bool Remove()
    {
        if (!File.Exists(SettingsPath) || Read() is not { } root) return false;
        var before = root.ToJsonString();
        RemoveOwn(root);
        return WriteIfChanged(root, before);
    }

    private static JsonObject? Read()
    {
        if (!File.Exists(SettingsPath)) return null;
        var text = File.ReadAllText(SettingsPath);
        if (string.IsNullOrWhiteSpace(text)) return [];
        // Refuse to touch a file that is not a JSON object instead of replacing it.
        return JsonNode.Parse(text, documentOptions: ReadOptions) as JsonObject
            ?? throw new InvalidDataException($"{SettingsPath} is not a JSON object.");
    }

    private static IEnumerable<string> Commands(JsonObject group) =>
        group["hooks"] is JsonArray hooks
            ? hooks.OfType<JsonObject>().Select(hook => hook["command"]?.GetValue<string>() ?? string.Empty)
            : [];

    private static bool IsOwn(JsonObject hook) =>
        hook["command"]?.GetValue<string>() is { } command &&
        command.Contains(Marker, StringComparison.Ordinal) &&
        command.Contains("hook.js", StringComparison.OrdinalIgnoreCase);

    private static void RemoveOwn(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) return;
        foreach (var name in hooks.Select(pair => pair.Key).ToArray())
        {
            if (hooks[name] is not JsonArray groups) continue;
            for (var index = groups.Count - 1; index >= 0; index--)
            {
                if (groups[index] is not JsonObject group || group["hooks"] is not JsonArray entries) continue;
                for (var entry = entries.Count - 1; entry >= 0; entry--)
                {
                    if (entries[entry] is JsonObject hook && IsOwn(hook)) entries.RemoveAt(entry);
                }
                if (entries.Count == 0) groups.RemoveAt(index);
            }
            if (groups.Count == 0) hooks.Remove(name);
        }
        if (hooks.Count == 0) root.Remove("hooks");
    }

    private static bool WriteIfChanged(JsonObject root, string before)
    {
        if (root.ToJsonString() == before) return false;
        Directory.CreateDirectory(Home);

        // One pristine copy of the user's settings, taken before the first change.
        var backup = SettingsPath + ".codex-presence.bak";
        if (File.Exists(SettingsPath) && !File.Exists(backup)) File.Copy(SettingsPath, backup);

        var temporary = SettingsPath + ".codex-presence.tmp";
        File.WriteAllText(temporary, root.ToJsonString(WriteOptions) + Environment.NewLine);
        File.Move(temporary, SettingsPath, overwrite: true);
        return true;
    }
}
