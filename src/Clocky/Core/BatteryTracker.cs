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
    private static readonly JsonSerializerOptions s_jsonOptions = new();
    private readonly string _filePath;
    private BatteryHistoryState _state = new();
    private DateTime _lastSampleTime = DateTime.MinValue;
    private DateTime _lastAnchorTime = DateTime.MinValue;
    private DateTime _lastDiskSaveTime = DateTime.MinValue;
    private float _lastPercent = -1f;
    private float _lastAnchorPercent = -1f;
    private bool _lastIsAc;
    private bool _trailingPointIsLive;
    private const int MaxStoredPoints = 2880; // 24h at 30s resolution

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

    public float FullCapacityWh { get; private set; } = 0f;
    public float DesignedCapacityWh { get; private set; } = 0f;
    public float HealthPercent
    {
        get
        {
            if (DesignedCapacityWh > 0 && FullCapacityWh > 0)
                return Math.Clamp((FullCapacityWh / DesignedCapacityWh) * 100.0f, 0f, 100f);
            return 100f;
        }
    }

    public BatteryTracker()
    {
        _filePath = GetBatteryHistoryFilePath();
        Load();
        QueryWmiBatteryStats();
    }

    public void UpdateCapacities(float fullCapWh, float designCapWh)
    {
        if (fullCapWh > 0) FullCapacityWh = fullCapWh;
        if (designCapWh > 0) DesignedCapacityWh = designCapWh;
    }

    private void QueryWmiBatteryStats()
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

        try
        {
            using var capSearcher = new ManagementObjectSearcher(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity");
            foreach (ManagementObject obj in capSearcher.Get())
            {
                if (obj["FullChargedCapacity"] != null && float.TryParse(obj["FullChargedCapacity"].ToString(), out float mwh) && mwh > 1000)
                {
                    FullCapacityWh = mwh / 1000.0f;
                    break;
                }
            }
        }
        catch { }

        try
        {
            using var designSearcher = new ManagementObjectSearcher(@"root\wmi", "SELECT DesignedCapacity FROM BatteryStaticData");
            foreach (ManagementObject obj in designSearcher.Get())
            {
                if (obj["DesignedCapacity"] != null && float.TryParse(obj["DesignedCapacity"].ToString(), out float mwh) && mwh > 1000)
                {
                    DesignedCapacityWh = mwh / 1000.0f;
                    break;
                }
            }
        }
        catch { }

        // Fallback: If DesignedCapacity is still 0, check Win32_Battery
        if (DesignedCapacityWh <= 0 || FullCapacityWh <= 0)
        {
            try
            {
                using var win32Searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT DesignCapacity, FullChargeCapacity FROM Win32_Battery");
                foreach (ManagementObject obj in win32Searcher.Get())
                {
                    if (FullCapacityWh <= 0 && obj["FullChargeCapacity"] != null && float.TryParse(obj["FullChargeCapacity"].ToString(), out float fc) && fc > 1000)
                    {
                        FullCapacityWh = fc / 1000.0f;
                    }
                    if (DesignedCapacityWh <= 0 && obj["DesignCapacity"] != null && float.TryParse(obj["DesignCapacity"].ToString(), out float dc) && dc > 1000)
                    {
                        DesignedCapacityWh = dc / 1000.0f;
                    }
                }
            }
            catch { }
        }

        // Final sanity fallback
        if (DesignedCapacityWh <= 0 && FullCapacityWh > 0)
        {
            DesignedCapacityWh = FullCapacityWh;
        }
    }

    public void AddSample(float percent, bool isAc, float rateWatts)
    {
        if (percent <= 0) return;

        var now = DateTime.Now;
        bool isGap = false;

        if (_lastSampleTime != DateTime.MinValue)
        {
            if ((now - _lastSampleTime).TotalSeconds > 30.0) // App was closed or asleep
            {
                isGap = true;
            }

            if (isAc && percent > _lastPercent && _lastPercent >= 0)
            {
                float gainedPercent = percent - _lastPercent;
                float gainedWh = (gainedPercent / 100f) * FullCapacityWh;
                _state.CumulativeChargedWh += gainedWh;
            }
        }

        _lastSampleTime = now;
        _lastPercent = percent;

        bool isStateChange = _state.Points.Count == 0 || isGap || isAc != _lastIsAc || Math.Abs(percent - _lastAnchorPercent) >= 0.5f;
        bool anchorDue = (now - _lastAnchorTime).TotalSeconds >= 30.0;

        if (isStateChange || anchorDue || _state.Points.Count < 2)
        {
            if (_trailingPointIsLive && !isGap && isAc == _lastIsAc && _state.Points.Count > 0)
            {
                // Promote live trailing point into the new 30s anchor
                var tail = _state.Points[^1];
                tail.Timestamp = now;
                tail.Percent = percent;
                tail.IsAc = isAc;
                tail.RateWatts = rateWatts;
                _trailingPointIsLive = false;
            }
            else
            {
                _state.Points.Add(new BatteryPoint
                {
                    Timestamp = now,
                    Percent = percent,
                    IsAc = isAc,
                    RateWatts = rateWatts,
                    IsGapStart = isGap
                });
                _trailingPointIsLive = false;
            }

            _lastAnchorTime = now;
            _lastAnchorPercent = percent;
            _lastIsAc = isAc;
        }
        else if (_trailingPointIsLive && _state.Points.Count > 0)
        {
            // Update trailing live point in-place without growing list every 1s
            var tail = _state.Points[^1];
            tail.Timestamp = now;
            tail.Percent = percent;
            tail.RateWatts = rateWatts;
        }
        else
        {
            _state.Points.Add(new BatteryPoint
            {
                Timestamp = now,
                Percent = percent,
                IsAc = isAc,
                RateWatts = rateWatts,
                IsGapStart = false
            });
            _trailingPointIsLive = true;
        }

        if (_state.Points.Count > MaxStoredPoints)
        {
            _state.Points.RemoveRange(0, _state.Points.Count - MaxStoredPoints);
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
                var loaded = JsonSerializer.Deserialize<BatteryHistoryState>(json, s_jsonOptions);
                if (loaded != null && loaded.Points != null)
                {
                    if (loaded.Points.Count > MaxStoredPoints)
                    {
                        // Downsample legacy 1-second history files to 30-second intervals while preserving gaps/transitions
                        var compacted = new List<BatteryPoint>(MaxStoredPoints);
                        DateTime lastKept = DateTime.MinValue;
                        bool lastAc = false;
                        for (int i = 0; i < loaded.Points.Count; i++)
                        {
                            var pt = loaded.Points[i];
                            if (i == 0 || i == loaded.Points.Count - 1 || pt.IsGapStart || pt.IsAc != lastAc || (pt.Timestamp - lastKept).TotalSeconds >= 30.0)
                            {
                                compacted.Add(pt);
                                lastKept = pt.Timestamp;
                                lastAc = pt.IsAc;
                            }
                        }
                        if (compacted.Count > MaxStoredPoints)
                        {
                            compacted.RemoveRange(0, compacted.Count - MaxStoredPoints);
                        }
                        loaded.Points = compacted;
                    }

                    _state = loaded;
                    if (_state.Points.Count > 0)
                    {
                        var last = _state.Points[^1];
                        _lastSampleTime = last.Timestamp;
                        _lastAnchorTime = last.Timestamp;
                        _lastPercent = last.Percent;
                        _lastAnchorPercent = last.Percent;
                        _lastIsAc = last.IsAc;
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
            string json = JsonSerializer.Serialize(_state, s_jsonOptions);
            File.WriteAllText(_filePath, json);
        }
        catch { }
    }
}
