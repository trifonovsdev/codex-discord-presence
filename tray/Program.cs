namespace CodexPresence;

static class Program
{
    private static string InstanceMutexName()
    {
        var identity = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity));
        return $"Local\\CodexDiscordPresence.Tray.{Convert.ToHexString(digest)[..16]}";
    }

    [STAThread]
    static void Main()
    {
        if (Environment.GetCommandLineArgs().Contains("--shutdown", StringComparer.OrdinalIgnoreCase))
        {
            var ownPath = Environment.ProcessPath;
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("CodexPresence"))
            {
                try
                {
                    if (process.Id != Environment.ProcessId && string.Equals(process.MainModule?.FileName, ownPath, StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill(true);
                        process.WaitForExit(3000);
                    }
                }
                catch { }
                finally { process.Dispose(); }
            }
            return;
        }
        using var mutex = new Mutex(true, InstanceMutexName(), out var createdNew);
        if (!createdNew) return;
        ApplicationConfiguration.Initialize();
        var showOnStart = !Environment.GetCommandLineArgs().Contains("--background", StringComparer.OrdinalIgnoreCase);
        Application.Run(new TrayApplicationContext(showOnStart));
    }
}
