using System.Text.Json.Nodes;
using CodexPresence;

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine($"PASS {message}");
}

var home = Path.Combine(Path.GetTempPath(), $"claude-hooks-{Guid.NewGuid():N}");
Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", home);
try
{
    Check(!ClaudeCodeHooks.Install(), "nothing is written when Claude Code is not installed");
    Check(!Directory.Exists(home), "a missing Claude profile is not created");

    Directory.CreateDirectory(home);
    var original = """
        {
          // Comments and trailing commas are tolerated.
          "model": "opus",
          "permissions": { "allow": ["Bash(npm test)"] },
          "hooks": {
            "PostToolUse": [
              { "matcher": "Bash", "hooks": [{ "type": "command", "command": "echo mine — ✓" }] }
            ],
          },
        }
        """;
    File.WriteAllText(ClaudeCodeHooks.SettingsPath, original);

    Check(ClaudeCodeHooks.Install(), "hooks are added to existing settings");
    Check(ClaudeCodeHooks.IsRegistered(), "every hook event is registered");
    Check(!ClaudeCodeHooks.Install(), "installing again is a no-op");
    Check(File.Exists(ClaudeCodeHooks.SettingsPath + ".codex-presence.bak"), "the untouched settings are backed up once");

    var written = File.ReadAllText(ClaudeCodeHooks.SettingsPath);
    var root = JsonNode.Parse(written)!.AsObject();
    Check(root["model"]!.GetValue<string>() == "opus" && root["permissions"]!["allow"]![0]!.GetValue<string>() == "Bash(npm test)",
        "unrelated settings are preserved");
    Check(written.Contains("echo mine — ✓"), "user hooks and non-ASCII text survive unescaped");
    var postToolUse = root["hooks"]!["PostToolUse"]!.AsArray();
    Check(postToolUse.Count == 2 && postToolUse[1]!["matcher"]!.GetValue<string>() == "Edit|MultiEdit|Write|NotebookEdit",
        "edit hooks are scoped to edit tools");
    Check(root["hooks"]!["UserPromptSubmit"]![0]!["hooks"]![0]!["command"]!.GetValue<string>().EndsWith("hook.js\" --agent claude"),
        "the command marks the Claude Code agent");
    Check(!ClaudeCodeHooks.Command.Contains('\\'), "commands use forward slashes for Git Bash and cmd.exe");

    Check(ClaudeCodeHooks.Remove(), "removal changes the file");
    var cleaned = JsonNode.Parse(File.ReadAllText(ClaudeCodeHooks.SettingsPath))!.AsObject();
    Check(cleaned["hooks"]!.AsObject().Count == 1 && cleaned["hooks"]!["PostToolUse"]!.AsArray().Count == 1,
        "only Codex Presence entries are removed");
    Check(!ClaudeCodeHooks.IsRegistered() && !ClaudeCodeHooks.Remove(), "removal is idempotent");

    File.WriteAllText(ClaudeCodeHooks.SettingsPath, "[1, 2]");
    var refused = false;
    try { ClaudeCodeHooks.Install(); } catch (InvalidDataException) { refused = true; }
    Check(refused && File.ReadAllText(ClaudeCodeHooks.SettingsPath) == "[1, 2]", "a settings file that is not an object is never replaced");
}
finally
{
    if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
}
