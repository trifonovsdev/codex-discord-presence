using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CodexPresence;

var root = Path.Combine(Path.GetTempPath(), "presence-client-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
listener.Stop();
var configPath = Path.Combine(root, "config.json");
File.WriteAllText(configPath, JsonSerializer.Serialize(new { port, presenceEnabled = true }));
var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true };
start.ArgumentList.Add(Path.GetFullPath("src/daemon.js"));
start.Environment["CODEX_HOME"] = root;
start.Environment["CODEX_PRESENCE_CONFIG"] = configPath;
start.Environment["CODEX_PRESENCE_TEST"] = "1";
using var process = Process.Start(start)!;
using var direct = new HttpClient(new HttpClientHandler { UseProxy = false });
var previousProxy = HttpClient.DefaultProxy;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine("PASS " + message);
}
try
{
    for (var attempt = 0; ; attempt++)
    {
        try { using var response = await direct.GetAsync($"http://127.0.0.1:{port}/health"); if (response.IsSuccessStatusCode) break; }
        catch (HttpRequestException) when (attempt < 60) { }
        if (attempt >= 60) throw new Exception("Test daemon did not start.");
        await Task.Delay(100);
    }
    var proxy = new RejectingProxy();
    HttpClient.DefaultProxy = proxy;
    using var daemon = new DaemonService(() => port);
    var health = await daemon.HealthAsync();
    Check(health?.Ok == true, "real daemon health reaches the tray even with an intercepting system proxy");
    Check(proxy.Requests == 0, "local status never passes through a proxy");
    await daemon.ControlAsync("pause");
    Check((await daemon.HealthAsync())?.PresenceEnabled == false, "pause travels directly to the local daemon");
    await daemon.ControlAsync("resume");
    Check((await daemon.HealthAsync())?.PresenceEnabled == true, "resume returns an accurate health snapshot");
    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    try { await daemon.HealthAsync(canceled.Token); throw new Exception("Expected cancellation."); }
    catch (OperationCanceledException) { Console.WriteLine("PASS caller cancellation is preserved"); }
    var requestedPort = 41001;
    var handler = new ReplyHandler();
    using var probe = new DaemonService(() => requestedPort, handler);
    Check((await probe.HealthAsync())?.Ok == true, "valid service contract is accepted");
    requestedPort = 41002;
    await probe.HealthAsync();
    Check(handler.LastPort == 41002, "external port changes are picked up on the next request");
    handler.Body = "{bad json";
    Check(await probe.HealthAsync() is null && probe.LastHealthError!.Contains("incompatible"), "malformed health reports a protocol error");
    handler.Body = "{\"ok\":false}";
    Check(await probe.HealthAsync() is null && probe.LastHealthError!.Contains("valid presence"), "a non-presence endpoint is rejected");
    handler.Status = HttpStatusCode.Forbidden;
    Check(await probe.HealthAsync() is null && probe.LastHealthError!.Contains("403"), "HTTP failure keeps its diagnostic status");
    handler.Status = HttpStatusCode.OK;
    handler.Body = "{\"ok\":true}";
    Check((await probe.HealthAsync())?.Ok == true && probe.LastHealthError is null, "reconnecting clears the previous error");

}
finally
{
    HttpClient.DefaultProxy = previousProxy;
    process.Kill(true);
    await process.WaitForExitAsync();
    Directory.Delete(root, true);
}

sealed class RejectingProxy : IWebProxy
{
    public int Requests { get; private set; }
    public ICredentials? Credentials { get; set; }
    public bool IsBypassed(Uri host) => false;
    public Uri GetProxy(Uri destination)
    {
        Requests++;
        return new Uri("http://127.0.0.1:1");
    }
}

sealed class ReplyHandler : HttpMessageHandler
{
    public string Body { get; set; } = "{\"ok\":true}";
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public int LastPort { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastPort = request.RequestUri!.Port;
        return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body) });
    }
}
