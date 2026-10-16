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

    private static readonly System.Text.RegularExpressions.Regex s_igpuSuffixRegex = new(
        @"\s*(?:with|w/)\s+(?:Radeon|Intel|AMD|Qualcomm|Adreno)?(?:\s+[\w\d]+)*\s+Graphics.*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    public static (string Type, int Speed, int ModuleCount, string Details) GetRamInfo()
    {
        if (_cachedRamInfo != null) return _cachedRamInfo.Value;

        string ramType = "";
        int configuredSpeed = 0;
        int ratedSpeed = 0;
        int moduleCount = 0;
        long totalCapacity = 0;

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, MemoryType FROM Win32_PhysicalMemory");
            foreach (ManagementObject mo in searcher.Get())
            {
                moduleCount++;
                if (long.TryParse(mo["Capacity"]?.ToString(), out long cap))
                    totalCapacity += cap;

                if (int.TryParse(mo["ConfiguredClockSpeed"]?.ToString(), out int cfgSpd) && cfgSpd > 0)
                    configuredSpeed = Math.Max(configuredSpeed, cfgSpd);
                if (int.TryParse(mo["Speed"]?.ToString(), out int spd) && spd > 0)
                    ratedSpeed = Math.Max(ratedSpeed, spd);

                if (int.TryParse(mo["SMBIOSMemoryType"]?.ToString(), out int smbiosType) && smbiosType > 0)
                {
                    ramType = smbiosType switch
                    {
                        20 => "DDR",
                        21 => "DDR2",
                        24 => "DDR3",
                        26 => "DDR4",
                        29 => "LPDDR3",
                        30 => "LPDDR4",
                        34 => "DDR5",
                        35 => "LPDDR5",
                        36 => "LPDDR5X",
                        _ => ramType
                    };
                }
                if (string.IsNullOrEmpty(ramType) && int.TryParse(mo["MemoryType"]?.ToString(), out int memType) && memType > 0)
                {
                    ramType = memType switch
                    {
                        20 => "DDR",
                        21 => "DDR2",
                        24 => "DDR3",
                        26 => "DDR4",
                        _ => ramType
                    };
                }
            }
        }
        catch { }

        int activeSpeed = configuredSpeed > 0 ? configuredSpeed : ratedSpeed;
        if (string.IsNullOrEmpty(ramType))
        {
            ramType = activeSpeed switch
            {
                >= 4400 => "DDR5",
                >= 2133 => "DDR4",
                >= 1066 => "DDR3",
                > 0 => "DDR",
                _ => "RAM"
            };
        }

        long totalGb = (long)Math.Round((double)totalCapacity / (1024.0 * 1024.0 * 1024.0));
        string details = totalGb > 0
            ? (moduleCount > 1
                ? $"{moduleCount}x {totalGb / Math.Max(1, moduleCount)} GB ({totalGb} GB {ramType})"
                : $"{totalGb} GB {ramType}")
            : ramType;

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
            .Replace("AMD Ryzen AI ", "Ryzen AI ")
            .Replace("AMD Ryzen ", "Ryzen ")
            .Replace("Snapdragon(R) ", "Snapdragon ")
            .Replace("Qualcomm ", "")
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

        // Strip integrated GPU marketing suffixes (e.g. "with Radeon 740M Graphics", "w/ Radeon Graphics")
        name = s_igpuSuffixRegex.Replace(name, "").Trim();

        return name.Length > 20 ? name.Substring(0, 18).TrimEnd() + "..." : name;
    }

    public static string GetShortGpuName(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return "GPU";
        string name = fullName
            .Replace("NVIDIA GeForce ", "")
            .Replace("NVIDIA ", "")
            .Replace(" Laptop GPU", "")
            .Replace("AMD Radeon(TM) ", "Radeon ")
            .Replace("AMD Radeon ", "")
            .Replace("AMD ", "")
            .Replace("Intel(R) Arc(TM) ", "Arc ")
            .Replace("Intel Arc ", "Arc ")
            .Replace("Intel(R) ", "")
            .Replace("Qualcomm(R) Adreno(TM) ", "Adreno ")
            .Replace(" Graphics", "")
            .Trim();

        return name.Length > 16 ? name.Substring(0, 14).TrimEnd() + "..." : name;
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

    private static System.Diagnostics.PerformanceCounterCategory? _procInfoCategory;
    private static bool _cpuCountersInitAttempted = false;
    private static float _cachedBaseFrequency = 0f;
    private static float _lastTotalCpuLoad = 0f;
    private static Dictionary<string, System.Diagnostics.CounterSample> _prevPerfSamples = new(64);
    private static Dictionary<string, System.Diagnostics.CounterSample> _currPerfSamples = new(64);
    private static System.Diagnostics.CounterSample _prevTotalUtilSample;
    private static bool _hasPrevTotalUtilSample;

    public static float LastTotalCpuLoad => _lastTotalCpuLoad;

    private static void EnsureCpuCategoryInitialized()
    {
        if (_cpuCountersInitAttempted) return;
        _cpuCountersInitAttempted = true;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("~MHz") is int mhz && mhz > 0)
            {
                _cachedBaseFrequency = mhz;
            }
        }
        catch { }

        if (_cachedBaseFrequency <= 0)
        {
            try
            {
                int count = Environment.ProcessorCount;
                var info = new PROCESSOR_POWER_INFORMATION[count];
                int size = System.Runtime.InteropServices.Marshal.SizeOf<PROCESSOR_POWER_INFORMATION>() * count;
                if (CallNtPowerInformation(ProcessorInformation, IntPtr.Zero, 0, info, size) == 0 && info[0].MaxMhz > 0)
                {
                    _cachedBaseFrequency = info[0].MaxMhz;
                }
            }
            catch { }
        }

        try
        {
            if (System.Diagnostics.PerformanceCounterCategory.Exists("Processor Information"))
            {
                _procInfoCategory = new System.Diagnostics.PerformanceCounterCategory("Processor Information");
            }
        }
        catch { _procInfoCategory = null; }
    }

    private static int ParseLogicalCoreIndex(ReadOnlySpan<char> inst)
    {
        int commaIdx = inst.IndexOf(',');
        if (commaIdx < 0) return -1;
        var groupSpan = inst.Slice(0, commaIdx);
        var procSpan = inst.Slice(commaIdx + 1);
        if (procSpan.Equals("_Total", StringComparison.OrdinalIgnoreCase)) return -1;
        if (!int.TryParse(groupSpan, out int group) || !int.TryParse(procSpan, out int proc)) return -1;
        return group * 64 + proc;
    }

    public static (float DynamicFreqMhz, Dictionary<int, float> CoreClocks) GetProcessorClocks()
    {
        EnsureCpuCategoryInitialized();

        float baseMhz = _cachedBaseFrequency;
        var coreClocks = new Dictionary<int, float>(Environment.ProcessorCount);
        float totalPerf = 0f;

        if (_procInfoCategory != null)
        {
            try
            {
                var catData = _procInfoCategory.ReadCategory();

                if (baseMhz <= 0 && catData.Contains("Processor Frequency"))
                {
                    var freqCol = catData["Processor Frequency"];
                    if (freqCol.Contains("_Total"))
                    {
                        long rawFreq = freqCol["_Total"].RawValue;
                        if (rawFreq > 0)
                        {
                            baseMhz = rawFreq;
                            _cachedBaseFrequency = baseMhz;
                        }
                    }
                }

                // Universal fallback for Total CPU Load when LHM driver is unavailable
                string? loadCounterName = catData.Contains("% Processor Utility")
                    ? "% Processor Utility"
                    : (catData.Contains("% Processor Time") ? "% Processor Time" : null);
                if (loadCounterName != null)
                {
                    var loadCol = catData[loadCounterName];
                    if (loadCol.Contains("_Total"))
                    {
                        var curLoadSample = loadCol["_Total"].Sample;
                        if (_hasPrevTotalUtilSample)
                        {
                            float calcLoad = System.Diagnostics.CounterSample.Calculate(_prevTotalUtilSample, curLoadSample);
                            if (float.IsFinite(calcLoad) && calcLoad >= 0f)
                                _lastTotalCpuLoad = Math.Clamp(calcLoad, 0f, 100f);
                        }
                        _prevTotalUtilSample = curLoadSample;
                        _hasPrevTotalUtilSample = true;
                    }
                }

                if (baseMhz > 0 && catData.Contains("% Processor Performance"))
                {
                    var perfCol = catData["% Processor Performance"];
                    _currPerfSamples.Clear();

                    foreach (System.Diagnostics.InstanceData id in perfCol.Values)
                    {
                        string instName = id.InstanceName;
                        _currPerfSamples[instName] = id.Sample;

                        if (_prevPerfSamples.TryGetValue(instName, out var prevSample))
                        {
                            float p = System.Diagnostics.CounterSample.Calculate(prevSample, id.Sample);
                            if (float.IsFinite(p) && p > 0f)
                            {
                                if (instName.Equals("_Total", StringComparison.OrdinalIgnoreCase))
                                {
                                    totalPerf = p;
                                }
                                else
                                {
                                    int coreIdx = ParseLogicalCoreIndex(instName.AsSpan());
                                    if (coreIdx >= 0)
                                    {
                                        coreClocks[coreIdx] = (float)Math.Round(baseMhz * (p / 100.0f), 0);
                                    }
                                }
                            }
                        }
                    }

                    (_prevPerfSamples, _currPerfSamples) = (_currPerfSamples, _prevPerfSamples);
                }
            }
            catch { }
        }

        // Fallback to CallNtPowerInformation if first sample or counters unavailable
        if (coreClocks.Count == 0)
        {
            try
            {
                int count = Environment.ProcessorCount;
                var info = new PROCESSOR_POWER_INFORMATION[count];
                int size = System.Runtime.InteropServices.Marshal.SizeOf<PROCESSOR_POWER_INFORMATION>() * count;
                if (CallNtPowerInformation(ProcessorInformation, IntPtr.Zero, 0, info, size) == 0)
                {
                    float sumMhz = 0f;
                    int valid = 0;
                    for (int i = 0; i < count; i++)
                    {
                        uint mhz = info[i].CurrentMhz > 0 ? info[i].CurrentMhz : info[i].MaxMhz;
                        if (mhz > 0)
                        {
                            coreClocks[i] = mhz;
                            sumMhz += mhz;
                            valid++;
                        }
                    }
                    if (valid > 0 && totalPerf <= 0f)
                    {
                        return ((float)Math.Round(sumMhz / valid, 0), coreClocks);
                    }
                }
            }
            catch { }
        }

        if (baseMhz <= 0)
        {
            return (0f, coreClocks);
        }

        float totalDynMhz = totalPerf > 0f
            ? (float)Math.Round(baseMhz * (totalPerf / 100.0f), 0)
            : (coreClocks.Count > 0 ? (float)Math.Round(coreClocks.Values.Average(), 0) : baseMhz);

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

    private static System.Diagnostics.PerformanceCounter? _thermalZoneCounter;
    private static bool _tzInitAttempted = false;

    public static float GetAcpiThermalZoneTemperature()
    {
        if (!_tzInitAttempted)
        {
            _tzInitAttempted = true;
            try
            {
                var cat = new System.Diagnostics.PerformanceCounterCategory("Thermal Zone Information");
                var insts = cat.GetInstanceNames();
                if (insts.Length > 0)
                {
                    try
                    {
                        _thermalZoneCounter = new System.Diagnostics.PerformanceCounter("Thermal Zone Information", "High Precision Temperature", insts[0], true);
                        _thermalZoneCounter.NextValue();
                    }
                    catch
                    {
                        _thermalZoneCounter = new System.Diagnostics.PerformanceCounter("Thermal Zone Information", "Temperature", insts[0], true);
                        _thermalZoneCounter.NextValue();
                    }
                }
            }
            catch { }
        }

        if (_thermalZoneCounter != null)
        {
            try
            {
                float val = _thermalZoneCounter.NextValue();
                if (val > 2000f) // High precision temperature in tenths of Kelvin (deci-Kelvin)
                {
                    float celsius = (val / 10.0f) - 273.15f;
                    if (celsius > 0f && celsius < 125f) return (float)Math.Round(celsius, 1);
                }
                else if (val > 200f) // Standard temperature in Kelvin
                {
                    float celsius = val - 273.15f;
                    if (celsius > 0f && celsius < 125f) return (float)Math.Round(celsius, 1);
                }
            }
            catch { }
        }

        return 0f;
    }
}
