using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CodexPresence;

public sealed class RemoteService
{
    private static readonly Regex HostPattern = new("^[A-Za-z0-9._@:-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex PathPattern = new("^[A-Za-z0-9_./~-]+$", RegexOptions.CultureInvariant);

    public async Task<(bool Ok, string Output)> TestAsync(RemoteHostConfig remote, CancellationToken cancellationToken = default)
    {
        var validation = Validate(remote);
        return validation is null
            ? await RunAsync("ssh.exe", ["-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=8", remote.Host, "python3", "--version"], cancellationToken)
            : (false, validation);
    }

    public async Task<(bool Ok, string Output)> InstallHelperAsync(RemoteHostConfig remote, CancellationToken cancellationToken = default)
    {
        var validation = Validate(remote);
        if (validation is not null) return (false, validation);
        if (!File.Exists(AppPaths.RemoteMonitorPath)) return (false, "remote-monitor.py is missing.");
        const string directory = "~/.local/share/CodexDiscordPresence";
        var create = await RunAsync("ssh.exe", ["-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=8", remote.Host, $"mkdir -p {directory}"], cancellationToken);
        if (!create.Ok) return create;
        var upload = await RunAsync("scp.exe", ["-q", AppPaths.RemoteMonitorPath, $"{remote.Host}:{remote.MonitorPath}"], cancellationToken);
        if (!upload.Ok) return upload;
        return await RunAsync("ssh.exe", ["-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=8", remote.Host, $"chmod 700 {remote.MonitorPath} && python3 -m py_compile {remote.MonitorPath}"], cancellationToken);
    }

    private static string? Validate(RemoteHostConfig remote)
    {
        if (string.IsNullOrWhiteSpace(remote.Host) || !HostPattern.IsMatch(remote.Host))
            return "SSH host may only contain letters, numbers, dots, colons, dashes, underscores, and @.";
        if (string.IsNullOrWhiteSpace(remote.MonitorPath) || !PathPattern.IsMatch(remote.MonitorPath))
            return "Remote monitor path contains unsupported characters.";
        return null;
    }

    private static async Task<(bool Ok, string Output)> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = $"{await stdout}\n{await stderr}".Trim();
            return (process.ExitCode == 0, string.IsNullOrWhiteSpace(output) ? $"Exit code {process.ExitCode}" : output);
        }
        catch (Exception error) { return (false, error.Message); }
    }
}
