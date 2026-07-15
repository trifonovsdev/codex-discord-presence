using Microsoft.Win32;
using System.Text.Json;

namespace CodexPresence;

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public PresenceConfig Load()
    {
        try
        {
            if (File.Exists(AppPaths.ConfigPath))
                return JsonSerializer.Deserialize<PresenceConfig>(File.ReadAllText(AppPaths.ConfigPath), JsonOptions) ?? new();
        }
        catch { }
        return new();
    }

    public void Save(PresenceConfig config)
    {
        Directory.CreateDirectory(AppPaths.AppDirectory);
        var temporary = AppPaths.ConfigPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(temporary, AppPaths.ConfigPath, true);
    }

    public bool StartsWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue("CodexDiscordPresence") is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (value) key.SetValue("CodexDiscordPresence", $"\"{Environment.ProcessPath}\" --background");
            else key.DeleteValue("CodexDiscordPresence", false);
        }
    }
}
