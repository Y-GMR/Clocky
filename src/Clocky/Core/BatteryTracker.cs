using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.Json;

namespace Clocky.Core;

public class BatteryPoint
{
    public DateTime Timestamp { get; set; }
    public float Percent { get; set; }
    public bool IsAc { get; set; }
    public float RateWatts { get; set; }
    public bool IsGapStart { get; set; }
}

public class BatteryHistoryState
{
    public int HardwareCycleCount { get; set; }
    public float CumulativeChargedWh { get; set; }
    public List<BatteryPoint> Points { get; set; } = new();
}

public class BatteryTracker
{
    private readonly string _filePath;
    private BatteryHistoryState _state = new();
    private DateTime _lastRecordTime = DateTime.MinValue;
    private DateTime _lastDiskSaveTime = DateTime.MinValue;
    private float _lastPercent = -1f;
    private const int MaxStoredPoints = 100000;

    public int HardwareCycleCount => _state.HardwareCycleCount;
    public float CumulativeChargedWh => _state.CumulativeChargedWh;
    public IReadOnlyList<BatteryPoint> History => _state.Points;

    public static string GetBatteryHistoryFilePath()
    {
        string appDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clocky");
        try
        {
            Directory.CreateDirectory(appDataFolder);
        }
        catch { }

        string targetFile = Path.Combine(appDataFolder, "battery_history.json");

        // Automatically migrate legacy file located next to executable if present
        try
        {
            string legacyFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "battery_history.json");
            if (File.Exists(legacyFile))
            {
                if (!File.Exists(targetFile))
                {
                    File.Move(legacyFile, targetFile);
                }
                else
                {
                    File.Delete(legacyFile);
                }
            }
        }
        catch { }

        return targetFile;
    }

    public BatteryTracker()
    {
        _filePath = GetBatteryHistoryFilePath();
        Load();
        QueryWmiCycleCount();
    }

    private void QueryWmiCycleCount()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM BatteryCycleCount");
            foreach (ManagementObject obj in searcher.Get())
            {
                if (obj["CycleCount"] != null)
                {
                    _state.HardwareCycleCount = Convert.ToInt32(obj["CycleCount"]);
                    break;
                }
            }
        }
        catch
        {
            if (_state.HardwareCycleCount == 0) _state.HardwareCycleCount = 0;
        }
    }

    public void AddSample(float percent, bool isAc, float rateWatts)
    {
        if (percent <= 0) return;

        var now = DateTime.Now;
        bool isGap = false;

        if (_lastRecordTime != DateTime.MinValue)
        {
            var delta = now - _lastRecordTime;
            if (delta.TotalSeconds > 15.0) // App was closed or offline
            {
                isGap = true;
            }

            if (isAc && percent > _lastPercent && _lastPercent >= 0)
            {
                float gainedPercent = percent - _lastPercent;
                float gainedWh = (gainedPercent / 100f) * 60.0f;
                _state.CumulativeChargedWh += gainedWh;
            }
        }

        _lastRecordTime = now;
        _lastPercent = percent;

        _state.Points.Add(new BatteryPoint
        {
            Timestamp = now,
            Percent = percent,
            IsAc = isAc,
            RateWatts = rateWatts,
            IsGapStart = isGap
        });

        while (_state.Points.Count > MaxStoredPoints)
        {
            _state.Points.RemoveAt(0);
        }

        // Ultra-Light I/O Optimization: Buffer disk writes to once every 60 seconds or on gap
        if ((now - _lastDiskSaveTime).TotalSeconds >= 60.0 || isGap)
        {
            Save();
            _lastDiskSaveTime = now;
        }
    }

    public void Flush()
    {
        Save();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                string json = File.ReadAllText(_filePath);
                var loaded = JsonSerializer.Deserialize<BatteryHistoryState>(json);
                if (loaded != null && loaded.Points != null)
                {
                    _state = loaded;
                    if (_state.Points.Count > 0)
                    {
                        _lastRecordTime = _state.Points.Last().Timestamp;
                        _lastPercent = _state.Points.Last().Percent;
                    }
                }
            }
        }
        catch { }
    }

    public void Save()
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };
            string json = JsonSerializer.Serialize(_state, options);
            File.WriteAllText(_filePath, json);
        }
        catch { }
    }
}
