using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace CodexPresence;

public sealed class UpdateService : IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    public Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(2, 4, 1);

    public UpdateService()
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"CodexPresence/{CurrentVersion}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>
    /// Reads the latest published release. Every field is probed rather than
    /// demanded: a release without assets or without a name used to throw a
    /// <see cref="KeyNotFoundException"/> out of the periodic update check.
    /// </summary>
    public async Task<ReleaseInfo?> CheckAsync(string repository, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"https://api.github.com/repos/{repository}/releases/latest", cancellationToken);
        if (!response.IsSuccessStatusCode) return null;

        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
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

    public async Task DownloadAndInstallAsync(ReleaseInfo release, CancellationToken cancellationToken = default)
    {
        if (release.InstallerUrl is null)
        {
            Process.Start(new ProcessStartInfo(release.PageUrl) { UseShellExecute = true });
            return;
        }
        if (release.ChecksumsUrl is null)
            throw new InvalidDataException("This release has no SHA256SUMS.txt manifest and will not be installed automatically.");

        var directory = Path.Combine(Path.GetTempPath(), "CodexPresenceUpdate", release.Version.ToString());
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "CodexPresenceSetup.exe");
        await File.WriteAllBytesAsync(destination, await http.GetByteArrayAsync(release.InstallerUrl, cancellationToken), cancellationToken);
        var sums = await http.GetStringAsync(release.ChecksumsUrl, cancellationToken);
        var expected = sums.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(parts => parts.Length >= 2 && parts[^1].Equals("CodexPresenceSetup.exe", StringComparison.OrdinalIgnoreCase))?.FirstOrDefault();
        if (expected is null || expected.Length != 64 || !expected.All(Uri.IsHexDigit))
            throw new InvalidDataException("SHA256SUMS.txt does not contain a valid checksum for CodexPresenceSetup.exe.");

        await using var stream = File.OpenRead(destination);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Downloaded installer checksum does not match the release manifest.");

        Process.Start(new ProcessStartInfo(destination, "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS") { UseShellExecute = true });
    }

    public void Dispose() => http.Dispose();
}
