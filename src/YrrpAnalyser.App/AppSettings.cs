using System.Text.Json;

namespace YrrpAnalyser.App;

/// <summary>Remembered between runs: recent files and where the rules INIs live.</summary>
internal sealed class AppSettings
{
    public List<string> RecentFiles { get; set; } = [];
    public List<string> RulesIniPaths { get; set; } = [];
    public string LastFolder { get; set; } = "";
    public bool ShowTimingEvents { get; set; }
    public bool ShowChatAndBeacons { get; set; } = true;

    private static string Path0 => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "YrrpAnalyser", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path0))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path0)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Settings are a convenience; a corrupt or unreadable file just means defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path0)!);
            File.WriteAllText(Path0, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 12) RecentFiles.RemoveRange(12, RecentFiles.Count - 12);
        LastFolder = Path.GetDirectoryName(path) ?? LastFolder;
    }
}
