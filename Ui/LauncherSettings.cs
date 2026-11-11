using System.Text.Json;

namespace BetterLC;

internal sealed class LauncherSettings
{
    public string Version { get; set; } = "26.1.2";
    public Dictionary<string, string> Modules { get; set; } = [];
    public ConnectionMode Mode { get; set; } = ConnectionMode.Offline;
    public string Server { get; set; } = "https://betterlc.ehko.dev";

    private static string FilePath => Path.Combine(HostPlatform.DataRoot, "launcher.json");

    public static LauncherSettings Load()
    {
        try { return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
