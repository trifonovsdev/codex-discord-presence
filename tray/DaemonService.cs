using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace CodexPresence;

public sealed class DaemonService : IDisposable
{
    private readonly Func<int> readPort;
    // Loopback status/control must not follow the user's internet proxy or redirects.
    private readonly HttpClient http;
    private Process? ownedProcess;
    public string? LastHealthError { get; private set; }

    public DaemonService(ConfigStore configStore) : this(() => configStore.Load().Port) { }

    internal DaemonService(Func<int> readPort, HttpMessageHandler? handler = null)
    {
        this.readPort = readPort;
        http = new HttpClient(handler ?? new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(3) };
    }

    /// <summary>Also follows configuration changes made by the installer or another tray instance.</summary>
    private Uri Endpoint(string path)
    {
        var port = readPort();
        if (port is < 1 or > 65535) port = 37642;
        return new Uri($"http://127.0.0.1:{port}{path}");
    }

    public async Task<HealthSnapshot?> HealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshot = await http.GetFromJsonAsync<HealthSnapshot>(Endpoint("/health"), cancellationToken);
            if (snapshot?.Ok != true)
            {
                LastHealthError = "The local endpoint did not return a valid presence status.";
                return null;
            }
            LastHealthError = null;
            return snapshot;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LastHealthError = "The local status request timed out. The app will retry automatically.";
        }
        catch (HttpRequestException error)
        {
            LastHealthError = error.StatusCode is { } status
                ? $"The local service returned HTTP {(int)status}."
                : "The app could not connect to the local status endpoint. It will retry automatically.";
        }
        catch (JsonException error)
        {
            LastHealthError = $"The local service returned an incompatible status (field: {error.Path ?? "$"}).";
        }
        return null;
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
