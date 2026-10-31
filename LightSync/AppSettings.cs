using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using LightSync.Sync;

namespace LightSync;

public static class AppPaths
{
    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LightSync");
}

public sealed class AppSettings
{
    public string DeviceAddress { get; set; } = "BE:58:94:02:AF:91";
    public bool AutoConnect { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool ResumeSyncOnLaunch { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool SyncWasRunning { get; set; }

    public string StaticColor { get; set; } = "#B400FF";
    public double Brightness { get; set; } = 100;
    public double EffectSpeed { get; set; } = 50;

    public SyncSettings Sync { get; set; } = new();

    static string FilePath => Path.Combine(AppPaths.DataDir, "settings.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            AppLog.Write($"Settings could not be read, using defaults: {ex.Message}");
        }
        return new AppSettings();
    }

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static void Save(string json)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Settings could not be saved: {ex.Message}");
        }
    }
}
