using System;
using System.Linq;
using System.Management;
using Microsoft.Win32;

namespace Clocky.Core;

public static class SystemHardwareHelper
{
    private static string? _cachedModelName;
    private static (string Type, int Speed, int ModuleCount, string Details)? _cachedRamInfo;

    public static string GetSystemModelName()
    {
        if (_cachedModelName != null) return _cachedModelName;

        try
        {
            // 1. Instant Registry Read from BIOS Information
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            if (key != null)
            {
                string family = (key.GetValue("SystemFamily") as string)?.Trim() ?? "";
                string version = (key.GetValue("SystemVersion") as string)?.Trim() ?? "";
                string product = (key.GetValue("SystemProductName") as string)?.Trim() ?? "";
                string manufacturer = (key.GetValue("SystemManufacturer") as string)?.Trim() ?? "";
                string board = (key.GetValue("BaseBoardProduct") as string)?.Trim() ?? "";

                bool IsValid(string s) => !string.IsNullOrWhiteSpace(s) && 
                    !s.Equals("Default string", StringComparison.OrdinalIgnoreCase) &&
                    !s.Equals("System Product Name", StringComparison.OrdinalIgnoreCase) &&
                    !s.Equals("None", StringComparison.OrdinalIgnoreCase) &&
                    !s.Equals("To be filled by O.E.M.", StringComparison.OrdinalIgnoreCase);

                string model = "";
                if (IsValid(family)) model = family;
                else if (IsValid(version)) model = version;
                else if (IsValid(product)) model = product;
                else if (IsValid(board)) model = board;

                if (!string.IsNullOrEmpty(model))
                {
                    if (IsValid(manufacturer) && !model.Contains(manufacturer, StringComparison.OrdinalIgnoreCase) &&
                        !manufacturer.Equals("System manufacturer", StringComparison.OrdinalIgnoreCase))
                    {
                        string cleanMfr = CleanManufacturerName(manufacturer);
                        _cachedModelName = $"{cleanMfr} {model}".Trim();
                    }
                    else
                    {
                        _cachedModelName = model;
                    }
                    return _cachedModelName;
                }
            }
        }
        catch { }

        _cachedModelName = "Windows PC";
        return _cachedModelName;
    }

    public static (string Type, int Speed, int ModuleCount, string Details) GetRamInfo()
    {
        if (_cachedRamInfo != null) return _cachedRamInfo.Value;

        string ramType = "DDR5";
        int configuredSpeed = 0;
        int ratedSpeed = 0;
        int moduleCount = 0;
        long totalCapacity = 0;

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, MemoryType, Manufacturer, PartNumber FROM Win32_PhysicalMemory");
            foreach (ManagementObject mo in searcher.Get())
            {
                moduleCount++;
                if (long.TryParse(mo["Capacity"]?.ToString(), out long cap))
                    totalCapacity += cap;

                if (int.TryParse(mo["ConfiguredClockSpeed"]?.ToString(), out int cfgSpd) && cfgSpd > 0)
                    configuredSpeed = Math.Max(configuredSpeed, cfgSpd);
                if (int.TryParse(mo["Speed"]?.ToString(), out int spd) && spd > 0)
                    ratedSpeed = Math.Max(ratedSpeed, spd);

                if (int.TryParse(mo["SMBIOSMemoryType"]?.ToString(), out int smbiosType))
                {
                    ramType = smbiosType switch
                    {
                        26 => "DDR4",
                        34 => "DDR5",
                        30 => "LPDDR4",
                        35 => "LPDDR5",
                        24 => "DDR3",
                        _ => ramType
                    };
                }
            }
        }
        catch { }

        long totalGb = (long)Math.Round((double)totalCapacity / (1024.0 * 1024.0 * 1024.0));
        string details = moduleCount > 1 
            ? $"{moduleCount}x {totalGb / Math.Max(1, moduleCount)} GB ({totalGb} GB {ramType})" 
            : $"{totalGb} GB {ramType}";

        int activeSpeed = configuredSpeed > 0 ? configuredSpeed : ratedSpeed;
        _cachedRamInfo = (ramType, activeSpeed, moduleCount, details);
        return _cachedRamInfo.Value;
    }

    public static string GetShortCpuName(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return "CPU";
        string name = fullName
            .Replace("Intel(R) Core(TM) Ultra ", "Core Ultra ")
            .Replace("Intel Core Ultra ", "Core Ultra ")
            .Replace("Intel(R) Core(TM) ", "")
            .Replace("Intel Core ", "")
            .Replace("Intel ", "")
            .Replace("AMD Ryzen ", "Ryzen ")
            .Replace("14th Gen ", "")
            .Replace("13th Gen ", "")
            .Replace("12th Gen ", "")
            .Replace("11th Gen ", "")
            .Replace("10th Gen ", "")
            .Replace(" 8-Core Processor", "")
            .Replace(" 6-Core Processor", "")
            .Replace(" 12-Core Processor", "")
            .Replace(" 16-Core Processor", "")
            .Replace(" 24-Core Processor", "")
            .Replace(" 32-Core Processor", "")
            .Replace(" Processor", "")
            .Replace(" processor", "")
            .Trim();

        return name.Length > 20 ? name.Substring(0, 20).Trim() : name;
    }

    public static string GetShortGpuName(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return "GPU";
        string name = fullName
            .Replace("NVIDIA GeForce ", "")
            .Replace("NVIDIA ", "")
            .Replace(" Laptop GPU", "")
            .Replace("AMD Radeon ", "")
            .Replace("AMD ", "")
            .Replace("Intel(R) Arc(TM) ", "Arc ")
            .Replace("Intel Arc ", "Arc ")
            .Replace("Intel(R) ", "")
            .Replace(" Graphics", "")
            .Trim();

        return name.Length > 16 ? name.Substring(0, 16).Trim() : name;
    }

    private static string CleanManufacturerName(string mfr)
    {
        string lower = mfr.ToLowerInvariant();
        if (lower.Contains("lenovo")) return "Lenovo";
        if (lower.Contains("asus")) return "ASUS";
        if (lower.Contains("dell") || lower.Contains("alienware")) return "Dell";
        if (lower.Contains("hp") || lower.Contains("hewlett")) return "HP";
        if (lower.Contains("micro-star") || lower.Contains("msi")) return "MSI";
        if (lower.Contains("gigabyte") || lower.Contains("aorus")) return "Gigabyte";
        if (lower.Contains("acer")) return "Acer";
        if (lower.Contains("razer")) return "Razer";
        if (lower.Contains("samsung")) return "Samsung";
        if (lower.Contains("microsoft")) return "Microsoft";
        if (lower.Contains("apple")) return "Apple";
        return mfr;
    }

    [System.Runtime.InteropServices.DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(
        int informationLevel,
        IntPtr lpInputBuffer,
        int nInputBufferSize,
        [System.Runtime.InteropServices.Out] PROCESSOR_POWER_INFORMATION[] lpOutputBuffer,
        int nOutputBufferSize);

    private const int ProcessorInformation = 11;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PROCESSOR_POWER_INFORMATION
    {
        public uint Number;
        public uint MaxMhz;
        public uint CurrentMhz;
        public uint MhzLimit;
        public uint MaxIdleState;
        public uint CurrentIdleState;
    }

    private static System.Diagnostics.PerformanceCounter? _baseFreqCounter;
    private static System.Diagnostics.PerformanceCounter? _totalPerfCounter;
    private static readonly Dictionary<int, System.Diagnostics.PerformanceCounter> _corePerfCounters = new();
    private static bool _cpuCountersInitAttempted = false;
    private static float _cachedBaseFrequency = 0f;

    private static void EnsureCpuCountersInitialized()
    {
        if (_cpuCountersInitAttempted) return;
        _cpuCountersInitAttempted = true;

        try
        {
            _baseFreqCounter = new System.Diagnostics.PerformanceCounter("Processor Information", "Processor Frequency", "_Total", true);
            _baseFreqCounter.NextValue();
        }
        catch { }

        try
        {
            _totalPerfCounter = new System.Diagnostics.PerformanceCounter("Processor Information", "% Processor Performance", "_Total", true);
            _totalPerfCounter.NextValue();
        }
        catch { }

        try
        {
            int count = Environment.ProcessorCount;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var pc = new System.Diagnostics.PerformanceCounter("Processor Information", "% Processor Performance", $"0,{i}", true);
                    pc.NextValue();
                    _corePerfCounters[i] = pc;
                }
                catch { }
            }
        }
        catch { }
    }

    public static (float DynamicFreqMhz, Dictionary<int, float> CoreClocks) GetProcessorClocks()
    {
        EnsureCpuCountersInitialized();

        float baseMhz = _cachedBaseFrequency;
        if (baseMhz <= 0 && _baseFreqCounter != null)
        {
            try
            {
                baseMhz = _baseFreqCounter.NextValue();
                if (baseMhz > 0) _cachedBaseFrequency = baseMhz;
            }
            catch { }
        }

        if (baseMhz <= 0)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                if (key?.GetValue("~MHz") is int mhz && mhz > 0)
                {
                    baseMhz = mhz;
                    _cachedBaseFrequency = baseMhz;
                }
            }
            catch { }
        }

        if (baseMhz <= 0)
        {
            try
            {
                int count = Environment.ProcessorCount;
                var info = new PROCESSOR_POWER_INFORMATION[count];
                int size = System.Runtime.InteropServices.Marshal.SizeOf<PROCESSOR_POWER_INFORMATION>() * count;
                if (CallNtPowerInformation(ProcessorInformation, IntPtr.Zero, 0, info, size) == 0 && info[0].MaxMhz > 0)
                {
                    baseMhz = info[0].MaxMhz;
                    _cachedBaseFrequency = baseMhz;
                }
            }
            catch { }
        }

        if (baseMhz <= 0) baseMhz = 2100f; // Sane baseline

        float totalPerf = 100f;
        if (_totalPerfCounter != null)
        {
            try
            {
                float v = _totalPerfCounter.NextValue();
                if (v > 0) totalPerf = v;
            }
            catch { }
        }

        float totalDynMhz = (float)Math.Round(baseMhz * (totalPerf / 100.0f), 0);

        var coreClocks = new Dictionary<int, float>();
        foreach (var kvp in _corePerfCounters)
        {
            try
            {
                float p = kvp.Value.NextValue();
                if (p > 0)
                {
                    coreClocks[kvp.Key] = (float)Math.Round(baseMhz * (p / 100.0f), 0);
                }
                else
                {
                    coreClocks[kvp.Key] = totalDynMhz;
                }
            }
            catch
            {
                coreClocks[kvp.Key] = totalDynMhz;
            }
        }

        if (coreClocks.Count == 0)
        {
            int count = Environment.ProcessorCount;
            for (int i = 0; i < count; i++) coreClocks[i] = totalDynMhz;
        }

        return (totalDynMhz, coreClocks);
    }

    private static System.Diagnostics.PerformanceCounter? _raplPkgCounter;
    private static System.Diagnostics.PerformanceCounter? _raplCoreCounter;
    private static bool _raplInitAttempted = false;

    public static (float PackagePowerWatts, float CoresPowerWatts) GetRaplPowerWatts()
    {
        if (!_raplInitAttempted)
        {
            _raplInitAttempted = true;
            try
            {
                var cat = new System.Diagnostics.PerformanceCounterCategory("Energy Meter");
                var insts = cat.GetInstanceNames();
                string? pkgInst = insts.FirstOrDefault(i => i.Contains("PKG", StringComparison.OrdinalIgnoreCase));
                string? coreInst = insts.FirstOrDefault(i => i.Contains("PP0", StringComparison.OrdinalIgnoreCase) || i.Contains("Core", StringComparison.OrdinalIgnoreCase));

                if (pkgInst != null)
                {
                    _raplPkgCounter = new System.Diagnostics.PerformanceCounter("Energy Meter", "Power", pkgInst, true);
                    _raplPkgCounter.NextValue();
                }
                if (coreInst != null)
                {
                    _raplCoreCounter = new System.Diagnostics.PerformanceCounter("Energy Meter", "Power", coreInst, true);
                    _raplCoreCounter.NextValue();
                }
            }
            catch { }
        }

        float pkgW = 0;
        float coresW = 0;

        if (_raplPkgCounter != null)
        {
            try
            {
                float mw = _raplPkgCounter.NextValue();
                if (mw > 0) pkgW = (float)Math.Round(mw / 1000.0f, 1);
            }
            catch { }
        }

        if (_raplCoreCounter != null)
        {
            try
            {
                float mw = _raplCoreCounter.NextValue();
                if (mw > 0) coresW = (float)Math.Round(mw / 1000.0f, 1);
            }
            catch { }
        }

        return (pkgW, coresW);
    }
}
