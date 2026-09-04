using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using CodexPresence;

var root = Path.Combine(Path.GetTempPath(), $"presence-updater-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
var payload = Encoding.UTF8.GetBytes("synthetic installer, never executed");
var hash = Convert.ToHexString(SHA256.HashData(payload));
var release = new ReleaseInfo(new Version(9, 0, 0), "Fixture", "https://github.com/example/repo/releases/tag/v9",
    "https://github.com/example/repo/releases/download/v9/CodexPresenceSetup.exe",
    "https://github.com/example/repo/releases/download/v9/SHA256SUMS.txt");
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException(label);
    Console.WriteLine($"PASS {label}");
}
try
{
    foreach (var binaryMarker in new[] { "", "*" })
    {
        ProcessStartInfo? started = null;
        using var client = new HttpClient(new FixtureHandler(request => request.RequestUri!.AbsolutePath.EndsWith(".txt")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"\uFEFF{hash.ToLowerInvariant()}  {binaryMarker}CodexPresenceSetup.exe\r\n") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        using var updater = new UpdateService(client, info =>
        {
            // This catches a Windows sharing violation if hashing still holds the file open.
            using var exclusive = File.Open(info.FileName, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Check(exclusive.Length == payload.Length, "complete verified file is unlocked before launch");
            started = info;
        }, root, Path.Combine(root, "Installed App"));
        await updater.DownloadAndInstallAsync(release);
        Check(started is not null && started.Arguments.Contains("/AUTOUPDATE=1"), "launch requests an explicit update restart");
        Check(started!.Arguments.Contains("/NORESTART") && started.Arguments.Contains("/DIR=\""), "launch preserves destination and prevents an OS restart");
    }

    foreach (var failure in new[] { "hash", "manifest", "http", "cancel", "launch" })
    {
        var launched = false;
        using var cancel = new CancellationTokenSource();
        using var client = new HttpClient(new FixtureHandler(request =>
        {
            if (failure == "http") return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (request.RequestUri!.AbsolutePath.EndsWith(".txt"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(failure == "manifest" ? "invalid" : $"{(failure == "hash" ? new string('0', 64) : hash)}  CodexPresenceSetup.exe") };
            if (failure == "cancel") cancel.Cancel();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        }));
        var directory = Path.Combine(root, failure);
        using var updater = new UpdateService(client, _ =>
        {
            if (failure == "launch") throw new System.ComponentModel.Win32Exception(5);
            launched = true;
        }, directory, root);
        try
        {
            await updater.DownloadAndInstallAsync(release, cancel.Token);
            throw new InvalidOperationException($"Expected {failure} to fail");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException or OperationCanceledException)
        {
            Check(!launched, $"{failure} never launches an unsafe or incomplete installer");
            Check(!Directory.Exists(directory) || !Directory.EnumerateFiles(directory, "*.part", SearchOption.AllDirectories).Any(), $"{failure} cleans partial downloads");
        }
    }

    var slowStarted = false;
    using (var slowClient = new HttpClient(new FixtureHandler(request => request.RequestUri!.AbsolutePath.EndsWith(".txt")
        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{hash}  CodexPresenceSetup.exe") }
        : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SlowStream(payload)) })))
    using (var slowUpdater = new UpdateService(slowClient, _ => slowStarted = true, root, root))
    {
        await slowUpdater.DownloadAndInstallAsync(release);
        Check(slowStarted, "download taking more than 30 seconds completes and launches");
    }

    using var unavailable = new HttpClient(new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
    using var checker = new UpdateService(unavailable, _ => { }, root, root);
    try
    {
        await checker.CheckAsync("example/repo");
        throw new InvalidOperationException("403 must not be reported as up to date");
    }
    catch (HttpRequestException) { Check(true, "API failures are distinguished from no updates"); }
}
finally { Directory.Delete(root, recursive: true); }

sealed class FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
}

sealed class SlowStream(byte[] bytes) : MemoryStream(bytes)
{
    private bool delayed;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!delayed)
        {
            delayed = true;
            await Task.Delay(TimeSpan.FromSeconds(31), cancellationToken);
        }
        return await base.ReadAsync(buffer, cancellationToken);
    }
}
