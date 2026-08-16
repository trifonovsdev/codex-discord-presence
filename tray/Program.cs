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

        ApplicationConfiguration.Initialize();
        if (arguments.Contains("--ui-smoke", StringComparer.OrdinalIgnoreCase))
        {
            try { RunUiSmoke(); }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                Environment.ExitCode = 1;
            }
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

        var showOnStart = !arguments.Contains("--background", StringComparer.OrdinalIgnoreCase);
        Application.Run(new TrayApplicationContext(showOnStart, activation));
    }

    private static void RunUiSmoke()
    {
        using var dashboard = new DashboardForm("smoke")
        {
            ShowInTaskbar = false,
            Opacity = 0,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-20_000, -20_000),
        };
        dashboard.UpdatePrivacy(new PrivacyConfig());
        dashboard.UpdateSnapshot(null);
        ShowAndValidate(dashboard);

        using var settings = new SettingsForm(new ConfigStore(), new RemoteService())
        {
            ShowInTaskbar = false,
            Opacity = 0,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-20_000, -20_000),
        };
        ShowAndValidate(settings, exerciseTabs: true);
    }

    private static void ShowAndValidate(Form form, bool exerciseTabs = false)
    {
        Rectangle? firstVisibleBounds = null;
        form.Shown += (_, _) => firstVisibleBounds = form.Bounds;
        form.Show();
        Application.DoEvents();
        form.PerformLayout();
        if (!form.IsHandleCreated || form.ClientSize.Width <= 0 || form.ClientSize.Height <= 0)
            throw new InvalidOperationException($"{form.Text} did not create a valid window.");
        if (form.FormBorderStyle is FormBorderStyle.None)
            throw new InvalidOperationException($"{form.Text} lost its native window frame.");
        if (firstVisibleBounds is null || firstVisibleBounds.Value != form.Bounds)
            throw new InvalidOperationException($"{form.Text} changed bounds after its first visible frame.");
        AssertNoHorizontalScroll(form);
        if (exerciseTabs)
        {
            var tabs = Descendants(form)
                .OfType<Button>()
                .Where(control => control.AccessibleRole == AccessibleRole.PageTab)
                .ToArray();
            if (tabs.Length != 3) throw new InvalidOperationException($"{form.Text} did not expose all settings tabs.");
            for (var pass = 0; pass < 2; pass++)
            {
                foreach (var tab in tabs)
                {
                    tab.PerformClick();
                    Application.DoEvents();
                    form.PerformLayout();
                    AssertNoHorizontalScroll(form);
                }
            }
        }
        form.Hide();
    }

    private static void AssertNoHorizontalScroll(Control root)
    {
        foreach (var scrollable in Descendants(root).Prepend(root).OfType<ScrollableControl>())
        {
            scrollable.PerformLayout();
            if (scrollable.HorizontalScroll.Visible)
                throw new InvalidOperationException($"{root.Text} exposes a horizontal scrollbar in {scrollable.GetType().Name}.");
        }
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
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
