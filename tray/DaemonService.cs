using System.Diagnostics;
using System.Net.Http.Json;

namespace CodexPresence;

public sealed class DaemonService : IDisposable
{
    private readonly ConfigStore configStore;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private Process? ownedProcess;
    private int? cachedPort;

    public DaemonService(ConfigStore configStore) => this.configStore = configStore;

    /// <summary>Forces the port to be re-read after the settings were saved.</summary>
    public void InvalidateEndpoint() => cachedPort = null;

    /// <summary>
    /// The port used to be re-read and re-parsed from disk on every health
    /// poll — twice a second, forever. It only changes when settings are saved.
    /// </summary>
    private Uri Endpoint(string path)
    {
        cachedPort ??= configStore.Load().Port;
        return new Uri($"http://127.0.0.1:{cachedPort}{path}");
    }

    public async Task<HealthSnapshot?> HealthAsync(CancellationToken cancellationToken = default)
    {
        try { return await http.GetFromJsonAsync<HealthSnapshot>(Endpoint("/health"), cancellationToken); }
        catch { return null; }
    }

    public async Task EnsureRunningAsync()
    {
        if (await HealthAsync() is not null) return;
        if (!File.Exists(AppPaths.DaemonPath)) throw new FileNotFoundException("daemon.js was not found", AppPaths.DaemonPath);

        var start = new ProcessStartInfo
        {
            FileName = AppPaths.NodePath,
            WorkingDirectory = AppPaths.AppDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add(AppPaths.DaemonPath);

        ownedProcess?.Dispose();
        ownedProcess = Process.Start(start);

        for (var attempt = 0; attempt < 15; attempt++)
        {
            await Task.Delay(200);
            if (await HealthAsync() is not null) return;
            if (ownedProcess is { HasExited: true })
            {
                throw new InvalidOperationException($"The presence daemon exited immediately (code {ownedProcess.ExitCode}). Check presence.log next to daemon.js.");
            }
        }
        throw new InvalidOperationException("Presence daemon did not become healthy.");
    }

    public async Task ControlAsync(string action)
    {
        using var response = await http.PostAsJsonAsync(Endpoint("/control"), new { action });
        response.EnsureSuccessStatusCode();
    }

    public async Task RestartAsync()
    {
        try { await ControlAsync("shutdown"); } catch { }
        await Task.Delay(500);
        await EnsureRunningAsync();
    }

    public async Task StopAsync()
    {
        try { await ControlAsync("shutdown"); } catch { }
        if (ownedProcess is { HasExited: false })
        {
            try { ownedProcess.WaitForExit(1500); } catch { }
        }
    }

    public void Dispose()
    {
        http.Dispose();
        ownedProcess?.Dispose();
    }
}
