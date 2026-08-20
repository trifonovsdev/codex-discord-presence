using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml;

namespace CodexPresence;

/// <summary>Application entry lifecycle; the WinUI XAML compiler generates Main.</summary>
public partial class App : Application
{
    private const string UiSmokeLogFileName = "codex-presence-ui-smoke.log";

    private Mutex? instanceMutex;
    private EventWaitHandle? activationSignal;
    private AppCoordinator? coordinator;
    private bool ownsMutex;
    private bool applicationExitStarted;

    public App()
    {
        try
        {
            if (IsUiSmokeMode()) WriteUiSmokeCheckpoint("App.InitializeComponent");
            InitializeComponent();
            UnhandledException += OnUnhandledException;
        }
        catch (Exception error)
        {
            if (IsUiSmokeMode()) WriteUiSmokeFailure("App.InitializeComponent", error);
            throw;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs();

        if (HasArgument(arguments, "--shutdown"))
        {
            ShutdownRunningInstances();
            ExitApplication(0);
            return;
        }

        if (HasArgument(arguments, "--ui-smoke"))
        {
            _ = RunUiSmokeAsync();
            return;
        }

        try
        {
            var key = InstanceKey();
            var mutex = new Mutex(true, $"Local\\CodexDiscordPresence.Tray.{key}", out var createdNew);
            var activation = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                $"Local\\CodexDiscordPresence.Show.{key}");

            if (!createdNew)
            {
                _ = activation.Set();
                activation.Dispose();
                mutex.Dispose();
                ExitApplication(0);
                return;
            }

            instanceMutex = mutex;
            activationSignal = activation;
            ownsMutex = true;

            var showOnStart = !HasArgument(arguments, "--background");
            coordinator = new AppCoordinator(showOnStart, activationSignal);
            coordinator.ExitCompleted += OnCoordinatorExitCompleted;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            ExitApplication(1);
        }
    }

    private static bool HasArgument(IEnumerable<string> arguments, string expected) =>
        arguments.Contains(expected, StringComparer.OrdinalIgnoreCase);

    private static bool IsUiSmokeMode() =>
        HasArgument(Environment.GetCommandLineArgs(), "--ui-smoke");

    /// <summary>Scoped to the install directory so portable copies stay independent.</summary>
    private static string InstanceKey()
    {
        var identity = AppPaths.BaseDirectory.ToUpperInvariant();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(digest)[..16];
    }

    private async Task RunUiSmokeAsync()
    {
        MainWindow? dashboard = null;
        SettingsWindow? settings = null;
        DiagnosticsWindow? doctor = null;
        var stage = "services";
        try
        {
            WriteUiSmokeCheckpoint(stage);
            var store = new ConfigStore();
            var remote = new RemoteService();
            using var daemon = new DaemonService(store);

            stage = "MainWindow.InitializeComponent";
            WriteUiSmokeCheckpoint(stage);
            dashboard = new MainWindow("smoke");
            dashboard.UpdatePrivacy(new PrivacyConfig());
            dashboard.UpdateSnapshot(null);

            stage = "SettingsWindow.InitializeComponent";
            WriteUiSmokeCheckpoint(stage);
            settings = new SettingsWindow(store, remote);

            stage = "DiagnosticsWindow.InitializeComponent";
            WriteUiSmokeCheckpoint(stage);
            doctor = new DiagnosticsWindow(new DiagnosticsService(daemon, store, remote));

            // Construction runs InitializeComponent for every top-level view.
            // The smoke path deliberately avoids starting daemon/network work.
            stage = "window cleanup";
            WriteUiSmokeCheckpoint(stage);
            await Task.Yield();
            doctor.Close();
            doctor = null;
            settings.Close();
            settings = null;
            dashboard.CloseForExit();
            dashboard = null;
            WriteUiSmokeCheckpoint("completed");
            ExitApplication(0);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            WriteUiSmokeFailure(stage, error);
            try { doctor?.Close(); } catch { }
            try { settings?.Close(); } catch { }
            try { dashboard?.CloseForExit(); } catch { }
            ExitApplication(1);
        }
    }

    private static string UiSmokeLogPath => Path.Combine(Path.GetTempPath(), UiSmokeLogFileName);

    private static void WriteUiSmokeCheckpoint(string stage)
    {
        try
        {
            File.WriteAllText(UiSmokeLogPath, $"Stage: {stage}{Environment.NewLine}");
        }
        catch
        {
            // Smoke diagnostics must never change application behavior.
        }
    }

    private static void WriteUiSmokeFailure(string stage, Exception error)
    {
        try
        {
            File.WriteAllText(
                UiSmokeLogPath,
                $"Stage: {stage}{Environment.NewLine}{error}{Environment.NewLine}");
        }
        catch
        {
            // Smoke diagnostics must never hide the original failure.
        }
    }

    private void OnCoordinatorExitCompleted(object? sender, EventArgs args)
    {
        if (coordinator is not null)
        {
            coordinator.ExitCompleted -= OnCoordinatorExitCompleted;
            coordinator.Dispose();
            coordinator = null;
        }
        ExitApplication(0);
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        Console.Error.WriteLine(args.Exception);
        if (IsUiSmokeMode()) WriteUiSmokeFailure("UnhandledException", args.Exception);
        args.Handled = true;

        if (coordinator is not null)
        {
            _ = coordinator.ExitAsync();
            return;
        }

        ExitApplication(1);
    }

    private void ExitApplication(int exitCode)
    {
        if (applicationExitStarted) return;
        applicationExitStarted = true;
        Environment.ExitCode = exitCode;

        if (coordinator is not null)
        {
            coordinator.ExitCompleted -= OnCoordinatorExitCompleted;
            coordinator.Dispose();
            coordinator = null;
        }

        activationSignal?.Dispose();
        activationSignal = null;

        if (ownsMutex && instanceMutex is not null)
        {
            try
            {
                instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The mutex was already released during startup failure cleanup.
            }
        }
        ownsMutex = false;
        instanceMutex?.Dispose();
        instanceMutex = null;

        UnhandledException -= OnUnhandledException;
        Exit();
    }

    private static void ShutdownRunningInstances()
    {
        var ownPath = Environment.ProcessPath;
        foreach (var process in Process.GetProcessesByName("CodexPresence"))
        {
            try
            {
                if (process.Id != Environment.ProcessId &&
                    string.Equals(process.MainModule?.FileName, ownPath, StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
            }
            catch
            {
                // Shutdown is best effort; installers can retry locked files.
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
