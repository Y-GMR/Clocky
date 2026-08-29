using System.IO;
using System.Text.Json;

namespace Clocky.Config;

public class AppConfig
{
    public int PollingIntervalMs { get; set; } = 1000;
    public string ThemePreference { get; set; } = "System";
    public bool StartWithWindows { get; set; } = false;
    public bool StartMinimized { get; set; } = false;
    public bool AlwaysOnTop { get; set; } = true;
    public bool EnableDebugLog { get; set; } = false;
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool MinimizeToTray { get; set; } = false;
    public bool ShowMainTrayIcon { get; set; } = true;
    public bool AutoCheckUpdates { get; set; } = true;
    public string UpdateFeedUrl { get; set; } = "https://raw.githubusercontent.com/Y-GMR/Clocky/master/version.json";
    public List<TraySensorConfig> TraySensors { get; set; } = new();

    public static string GetConfigFilePath()
    {
        try
        {
            // 1. Prioritize portable config in BaseDirectory if it already exists or is writable
            string localFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "clocky_config.json");
            if (File.Exists(localFile))
            {
                return localFile;
            }
        }
        catch { }

        // 2. Standard Windows User Directory: %LocalAppData%\Clocky\clocky_config.json
        string appDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clocky");
        try
        {
            Directory.CreateDirectory(appDataFolder);
        }
        catch { }

        return Path.Combine(appDataFolder, "clocky_config.json");
    }

    public static AppConfig LoadDefault()
    {
        return new AppConfig
        {
            PollingIntervalMs = 1000,
            StartMinimized = false,
            MinimizeToTrayOnClose = true,
            AutoCheckUpdates = true,
            UpdateFeedUrl = "https://raw.githubusercontent.com/Y-GMR/Clocky/master/version.json",
            TraySensors = new List<TraySensorConfig>
            {
                new() { Id = "cpu.temp", Label = "CPU Temp", SensorType = "Temperature", Enabled = true, BackgroundColorHex = "#0284C7", Unit = "°C", Order = 1 },
                new() { Id = "gpu.temp", Label = "GPU Temp", SensorType = "Temperature", Enabled = true, BackgroundColorHex = "#16A34A", Unit = "°C", Order = 2 },
                new() { Id = "cpu.power", Label = "CPU Power", SensorType = "Power", Enabled = true, BackgroundColorHex = "#1E40AF", Unit = "W", Order = 3 },
                new() { Id = "gpu.power", Label = "GPU Power", SensorType = "Power", Enabled = true, BackgroundColorHex = "#14532D", Unit = "W", Order = 4 },
                new() { Id = "system.power", Label = "Total Power", SensorType = "Power", Enabled = true, BackgroundColorHex = "#0F172A", Unit = "W", Order = 5 },
                new() { Id = "battery.life", Label = "Battery Life", SensorType = "BatteryTime", Enabled = true, BackgroundColorHex = "#475569", Unit = "h", Order = 6 }
            }
        };
    }

    public static AppConfig Load()
    {
        string path = GetConfigFilePath();
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var config = JsonSerializer.Deserialize<AppConfig>(json);
                if (config != null)
                {
                    if (string.IsNullOrWhiteSpace(config.UpdateFeedUrl) || config.UpdateFeedUrl.Contains("IwangPetra") || config.UpdateFeedUrl.Contains("/main/"))
                    {
                        config.UpdateFeedUrl = "https://raw.githubusercontent.com/Y-GMR/Clocky/master/version.json";
                        config.Save();
                    }
                    return config;
                }
            }
        }
        catch { }

        var def = LoadDefault();
        def.Save();
        return def;
    }

    public void Save()
    {
        string path = GetConfigFilePath();
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            string tempPath = path + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }
        catch { }
    }
}
