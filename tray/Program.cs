namespace CodexPresence;

static class Program
{
    /// <summary>Scoped to the install directory so portable and installed copies stay independent.</summary>
    private static string InstanceKey()
    {
        var identity = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(digest)[..16];
    }

    [STAThread]
    static void Main()
    {
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Contains("--shutdown", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownRunningInstances();
            return;
        }

        var key = InstanceKey();
        using var mutex = new Mutex(true, $"Local\\CodexDiscordPresence.Tray.{key}", out var createdNew);
        using var activation = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\CodexDiscordPresence.Show.{key}");

        if (!createdNew)
        {
            // Launching the app again brings the existing window forward
            // instead of appearing to do nothing at all.
            activation.Set();
            return;
        }

        ApplicationConfiguration.Initialize();
        var showOnStart = !arguments.Contains("--background", StringComparer.OrdinalIgnoreCase);
        Application.Run(new TrayApplicationContext(showOnStart, activation));
    }

    private static void ShutdownRunningInstances()
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
    }
}
