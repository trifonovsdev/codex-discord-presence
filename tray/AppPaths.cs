namespace CodexPresence;

public static class AppPaths
{
    public static string BaseDirectory
    {
        get
        {
            var processPath = Environment.ProcessPath;
            var executableDirectory = string.IsNullOrWhiteSpace(processPath)
                ? null
                : Path.GetDirectoryName(processPath);
            return Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(executableDirectory ?? AppContext.BaseDirectory));
        }
    }

    public static string AppDirectory => Directory.Exists(Path.Combine(BaseDirectory, "app"))
        ? Path.Combine(BaseDirectory, "app")
        : FindRepositoryRoot() is { } root ? Path.Combine(root, "src") : BaseDirectory;
    public static string ConfigPath => Path.Combine(AppDirectory, "config.json");
    public static string DaemonPath => Path.Combine(AppDirectory, "daemon.js");
    public static string HookPath => Path.Combine(AppDirectory, "hook.js");
    public static string RemoteMonitorPath => Path.Combine(AppDirectory, "remote-monitor.py");
    public static string NodePath => File.Exists(Path.Combine(BaseDirectory, "runtime", "node.exe"))
        ? Path.Combine(BaseDirectory, "runtime", "node.exe")
        : "node.exe";
    public static string StatePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "CodexPresence", "state.json");
    public static string HooksPath => Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), "hooks.json");

    private static string? FindRepositoryRoot()
    {
        var current = new DirectoryInfo(BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "src", "daemon.js"))) return current.FullName;
            current = current.Parent;
        }
        return null;
    }
}
