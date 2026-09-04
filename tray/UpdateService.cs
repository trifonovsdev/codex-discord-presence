using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace CodexPresence;

public sealed class UpdateService : IDisposable
{
    private readonly HttpClient http;
    private readonly Action<ProcessStartInfo> launch;
    private readonly string updateDirectory;
    private readonly string installDirectory;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);
    public Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(2, 5, 1);

    public UpdateService() : this(new HttpClient(), info =>
    {
        using var process = Process.Start(info)
            ?? throw new IOException("Windows did not start the installer.");
    }, Path.Combine(Path.GetTempPath(), "CodexPresenceUpdate"), AppPaths.BaseDirectory)
    {
    }

    internal UpdateService(HttpClient http, Action<ProcessStartInfo> launch, string updateDirectory, string installDirectory)
    {
        this.http = http;
        this.launch = launch;
        this.updateDirectory = updateDirectory;
        this.installDirectory = installDirectory;
        // A metadata deadline must not become the deadline for a 100 MB installer.
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"CodexPresence/{CurrentVersion}");
    }

    /// <summary>
    /// Reads the latest published release. Every field is probed rather than
    /// demanded: a release without assets or without a name used to throw a
    /// <see cref="KeyNotFoundException"/> out of the periodic update check.
    /// </summary>
    public async Task<ReleaseInfo?> CheckAsync(string repository, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(deadline.Token), cancellationToken: deadline.Token);
        var root = document.RootElement;
        if (!root.TryGetProperty("tag_name", out var tagElement)) return null;
        if (!Version.TryParse(tagElement.GetString()?.TrimStart('v', 'V'), out var version)) return null;

        string? installer = null;
        string? checksums = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
                var url = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;
                if (name.EndsWith("Setup.exe", StringComparison.OrdinalIgnoreCase)) installer = url;
                if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) checksums = url;
            }
        }

        var pageUrl = root.TryGetProperty("html_url", out var page) ? page.GetString() : null;
        if (pageUrl is null) return null;
        var name0 = root.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() : null;
        return new ReleaseInfo(version, string.IsNullOrWhiteSpace(name0) ? $"v{version}" : name0, pageUrl, installer, checksums);
    }

    public async Task DownloadAndInstallAsync(ReleaseInfo release, CancellationToken cancellationToken = default,
        IProgress<UpdateProgress>? progress = null)
    {
        if (release.InstallerUrl is null || release.ChecksumsUrl is null)
            throw new InvalidDataException("This release is missing its installer or SHA256SUMS.txt. Download it from the release page when both files are available.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(DownloadTimeout);
        var token = deadline.Token;
        var directory = Path.Combine(updateDirectory, release.Version.ToString(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "CodexPresenceSetup.exe");
        var partial = destination + ".part";
        var stage = "read the release checksum";
        try
        {
            progress?.Report(new UpdateProgress("Reading release checksum", null));
            var sums = await http.GetStringAsync(release.ChecksumsUrl, token);
            var expected = sums.TrimStart('\uFEFF').Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .FirstOrDefault(parts => parts.Length == 2 && parts[1].TrimStart('*').Equals("CodexPresenceSetup.exe", StringComparison.OrdinalIgnoreCase))?[0];
            if (expected is null || expected.Length != 64 || !expected.All(Uri.IsHexDigit))
                throw new InvalidDataException("SHA256SUMS.txt does not contain a valid checksum for CodexPresenceSetup.exe.");

            stage = "download the installer";
            using (var response = await http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[81920];
                long received = 0;
                var lastReport = Stopwatch.StartNew();
                progress?.Report(new UpdateProgress("Downloading update", 0));
                int read;
                while ((read = await input.ReadAsync(buffer, token)) != 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    received += read;
                    if (lastReport.ElapsedMilliseconds >= 150)
                    {
                        progress?.Report(new UpdateProgress("Downloading update", total > 0 ? Math.Min(100, received * 100d / total.Value) : null));
                        lastReport.Restart();
                    }
                }
                if (total is not null && received != total.Value)
                    throw new InvalidDataException("The download ended before the complete installer arrived. Try again.");
            }

            stage = "verify the installer";
            progress?.Report(new UpdateProgress("Verifying download", null));
            string actual;
            // Dispose the hash handle before Windows/Inno Setup opens the executable.
            await using (var stream = File.OpenRead(partial))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Downloaded installer checksum does not match the release manifest. Nothing was installed; try downloading again.");
            token.ThrowIfCancellationRequested();
            File.Move(partial, destination);

            stage = "start the installer";
            progress?.Report(new UpdateProgress("Starting installer", null));
            var log = Path.Combine(directory, "install.log");
            launch(new ProcessStartInfo(destination,
                $"/SILENT /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /AUTOUPDATE=1 /DIR=\"{installDirectory}\" /LOG=\"{log}\"")
            { UseShellExecute = true });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The update download timed out after 10 minutes. Check the connection and try again.");
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            throw new IOException($"Windows could not start the verified installer: {error.Message}. Open the GitHub release to install it manually.", error);
        }
        catch (HttpRequestException error)
        {
            throw new HttpRequestException($"Could not {stage}: {error.Message}. Check the connection and try again.", error, error.StatusCode);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    public void Dispose() => http.Dispose();
}

public sealed record UpdateProgress(string Stage, double? Percent);
