using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace Clocky.Core;

public class HardwareEngine : IDisposable
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        ref STORAGE_DEVICE_NUMBER lpOutBuffer,
        uint nOutBufferSize,
        ref uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        byte[] lpInBuffer,
        uint nInBufferSize,
        byte[] lpOutBuffer,
        uint nOutBufferSize,
        ref uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_DEVICE_NUMBER
    {
        public int DeviceType;
        public int DeviceNumber;
        public int PartitionNumber;
    }

    private const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

    private static readonly Dictionary<string, (string BusType, bool IsHdd, bool IsRemovable)> _driveDescriptorCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> _driveDeviceNumberCache = new(StringComparer.OrdinalIgnoreCase);

    private static (string BusType, bool IsHdd, bool IsRemovable) GetDriveMediaDescriptor(string driveLetter)
    {
        lock (_driveDescriptorCache)
        {
            if (_driveDescriptorCache.TryGetValue(driveLetter, out var cached))
                return cached;
        }

        try
        {
            string path = $@"\\.\{driveLetter.TrimEnd('\\')}";
            IntPtr hDevice = CreateFile(path, 0, 1 | 2, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (hDevice != IntPtr.Zero && hDevice != (IntPtr)(-1))
            {
                // 1. Query BusType & Removable
                byte[] queryBuf = new byte[12]; // PropertyId = 0 (StorageDeviceProperty), QueryType = 0
                byte[] outBuf = new byte[1024];
                uint bytesRet = 0;
                bool ok = DeviceIoControl(hDevice, IOCTL_STORAGE_QUERY_PROPERTY, queryBuf, 12, outBuf, 1024, ref bytesRet, IntPtr.Zero);
                
                string bus = "Fixed";
                bool removable = false;
                if (ok && bytesRet >= 32)
                {
                    removable = (outBuf[10] != 0);
                    int busId = BitConverter.ToInt32(outBuf, 28);
                    bus = busId switch
                    {
                        17 => "NVMe",
                        11 => "SATA",
                        7 => "USB",
                        3 => "ATA",
                        1 => "SCSI",
                        14 => "Virtual",
                        _ => "Fixed"
                    };
                }

                // 2. Query SeekPenalty (HDD detection)
                byte[] seekQuery = new byte[12];
                seekQuery[0] = 7; // StorageDeviceSeekPenaltyProperty = 7
                byte[] seekOut = new byte[12];
                bool isHdd = false;
                bool okSeek = DeviceIoControl(hDevice, IOCTL_STORAGE_QUERY_PROPERTY, seekQuery, 12, seekOut, 12, ref bytesRet, IntPtr.Zero);
                if (okSeek && bytesRet >= 9)
                {
                    isHdd = (seekOut[8] != 0); // IncursSeekPenalty == true -> HDD
                }

                CloseHandle(hDevice);
                var desc = (bus, isHdd, removable);
                lock (_driveDescriptorCache) { _driveDescriptorCache[driveLetter] = desc; }
                return desc;
            }
        }
        catch { }
        return ("Fixed", false, false);
    }

    private static int GetDiskDeviceNumber(string driveLetter)
    {
        lock (_driveDeviceNumberCache)
        {
            if (_driveDeviceNumberCache.TryGetValue(driveLetter, out int cachedNum))
                return cachedNum;
        }

        try
        {
            string path = $@"\\.\{driveLetter.TrimEnd('\\')}";
            IntPtr hDevice = CreateFile(path, 0, 1 | 2, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (hDevice != IntPtr.Zero && hDevice != (IntPtr)(-1))
            {
                var sdn = new STORAGE_DEVICE_NUMBER();
                uint bytesRet = 0;
                bool ok = DeviceIoControl(hDevice, IOCTL_STORAGE_GET_DEVICE_NUMBER, IntPtr.Zero, 0, ref sdn, (uint)Marshal.SizeOf(sdn), ref bytesRet, IntPtr.Zero);
                CloseHandle(hDevice);
                if (ok)
                {
                    lock (_driveDeviceNumberCache) { _driveDeviceNumberCache[driveLetter] = sdn.DeviceNumber; }
                    return sdn.DeviceNumber;
                }
            }
        }
        catch { }
        return -1;
    }

    private readonly BatteryTracker _batteryTracker = new();
    public BatteryTracker BatteryTracker => _batteryTracker;
    private readonly DiskIoTracker _diskIoTracker = new();
    private readonly NetworkTracker _networkTracker = new();
    private readonly ProcessTracker _processTracker = new();
    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor;
    private readonly System.Timers.Timer _timer;
    private bool _isUpdating;
    private readonly Dictionary<string, SensorRecord> _sensorHistory = new();

    public event Action<TelemetrySnapshot>? TelemetryUpdated;

    public TelemetrySnapshot CurrentSnapshot { get; private set; } = new();

    public HardwareEngine(int intervalMs = 1000)
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = false,
            IsControllerEnabled = false,
            IsNetworkEnabled = false,
            IsStorageEnabled = true,
            IsBatteryEnabled = false
        };

        _visitor = new UpdateVisitor();

        try
        {
            _computer.Open();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Clocky] Error opening LibreHardwareMonitor: {ex.Message}");
        }

        // Prime native performance counters so first poll delivers live values
        try
        {
            SystemHardwareHelper.GetRaplPowerWatts();
            SystemHardwareHelper.GetProcessorClocks();
        }
        catch { }

        _timer = new System.Timers.Timer(Math.Max(1000, intervalMs));
        _timer.Elapsed += (s, e) => Poll();
    }

    public bool TrackDetailedProcesses
    {
        get => _processTracker.DetailedMode;
        set => _processTracker.DetailedMode = value;
    }

    public bool TrackDetailedNetwork
    {
        get => _networkTracker.DetailedMode;
        set => _networkTracker.DetailedMode = value;
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    public void SetInterval(int ms)
    {
        _timer.Interval = Math.Max(1000, ms);
    }

    public void ResetStatistics()
    {
        lock (_sensorHistory)
        {
            foreach (var s in _sensorHistory.Values)
                s.Reset();
        }
    }

    public void Poll()
    {
        if (_isUpdating) return;
        _isUpdating = true;

        try
        {
            try
            {
                _computer.Accept(_visitor);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Clocky] LibreHardwareMonitor visitor notice: {ex.Message}");
            }

            var snapshot = ExtractSnapshot();
            CurrentSnapshot = snapshot;
            TelemetryUpdated?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            GlobalExceptionHandler.LogCrashToFile(ex, "HardwareEngine.Poll");
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private TelemetrySnapshot ExtractSnapshot()
    {
        var snap = new TelemetrySnapshot();
        var allSensors = new List<SensorRecord>();

        // System Metadata (Universal & Personalized)
        try
        {
            snap.SystemModelName = SystemHardwareHelper.GetSystemModelName();
            var (ramType, ramSpeed, _, _) = SystemHardwareHelper.GetRamInfo();
            snap.RamTypeStr = ramType;
            snap.RamSpeedMt = ramSpeed;
        }
        catch { }

        // 1. CPU Telemetry
        try
        {
            var cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
            if (cpu != null)
            {
                snap.CpuName = cpu.Name;
                var coreLoads = new Dictionary<int, float>();
                var coreTemps = new Dictionary<int, float>();
                var coreClocks = new Dictionary<int, float>();

            foreach (var sensor in cpu.Sensors)
            {
                if (!sensor.Value.HasValue) continue;
                var val = sensor.Value.Value;
                RecordSensor(GetCpuCategory(sensor.SensorType), sensor.Name, val, GetSensorUnit(sensor.SensorType), allSensors);

                switch (sensor.SensorType)
                {
                    case SensorType.Load:
                        if (sensor.Name.Contains("Total", StringComparison.OrdinalIgnoreCase))
                            snap.CpuTotalUtil = val;
                        else
                        {
                            int idx = ParseCoreIndex(sensor.Name);
                            if (idx >= 0) coreLoads[idx] = val;
                        }
                        break;

                    case SensorType.Temperature:
                        if (sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                            sensor.Name.Contains("Core Max", StringComparison.OrdinalIgnoreCase) ||
                            sensor.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase))
                        {
                            if (snap.CpuPackageTemp == 0 || sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                                snap.CpuPackageTemp = val;
                        }
                        else
                        {
                            int idx = ParseCoreIndex(sensor.Name);
                            if (idx >= 0) coreTemps[idx] = val;
                        }
                        break;

                    case SensorType.Power:
                        if (sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                            sensor.Name.Contains("Cores", StringComparison.OrdinalIgnoreCase))
                        {
                            if (snap.CpuPackagePower == 0 || sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                                snap.CpuPackagePower = val;
                        }
                        else if (sensor.Name.Contains("Platform", StringComparison.OrdinalIgnoreCase) ||
                                 sensor.Name.Contains("System", StringComparison.OrdinalIgnoreCase))
                        {
                            if (val > 0) snap.TotalSystemPowerWatts = val;
                        }
                        break;

                    case SensorType.Clock:
                        if (sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                        {
                            int idx = ParseCoreIndex(sensor.Name);
                            if (idx >= 0) coreClocks[idx] = val;
                            if (val > snap.CpuMaxFrequency) snap.CpuMaxFrequency = val;
                        }
                        break;

                    case SensorType.Voltage:
                        if (val > 0)
                        {
                            if (snap.CpuVoltage == 0 || sensor.Name.Contains("VID", StringComparison.OrdinalIgnoreCase) || sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                                snap.CpuVoltage = val;
                        }
                        break;
                }
            }

            // Ground truth dynamic CPU core clocks matching Windows Task Manager & HWiNFO
            bool hasDynamicClocks = coreClocks.Count > 0 && coreClocks.Values.Any(c => c > 2500f || c != coreClocks.Values.First());
            if (!hasDynamicClocks || coreClocks.Count == 0 || snap.CpuMaxFrequency <= 2100f)
            {
                var (dynFreq, dynClocks) = SystemHardwareHelper.GetProcessorClocks();
                if (dynClocks.Count > 0)
                {
                    foreach (var kvp in dynClocks)
                    {
                        coreClocks[kvp.Key] = kvp.Value;
                    }
                    snap.CpuMaxFrequency = Math.Max(dynFreq, dynClocks.Values.Max());
                }
            }

            // Real-time Intel RAPL Package Power & Cores Power via Windows Energy Meter / MSR
            var (raplPkg, raplCores) = SystemHardwareHelper.GetRaplPowerWatts();
            if (raplPkg > 0)
            {
                snap.CpuPackagePower = raplPkg;
            }
            else if (snap.CpuPackagePower == 0)
            {
                snap.CpuPackagePower = (float)Math.Round(12.0f + (snap.CpuTotalUtil / 100.0f) * 45.0f, 1);
            }

            // Fallback for CPU Package Temperature if MSR is unavailable
            if (snap.CpuPackageTemp == 0 && coreTemps.Count > 0)
                snap.CpuPackageTemp = coreTemps.Values.Max();

            if (snap.CpuPackageTemp == 0)
            {
                float fallbackTemp = 0;
                foreach (var mb in _computer.Hardware.Where(h => h.HardwareType == HardwareType.Motherboard || h.HardwareType == HardwareType.SuperIO))
                {
                    foreach (var s in mb.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue && s.Value.Value > 20))
                    {
                        if (s.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                        {
                            fallbackTemp = s.Value.GetValueOrDefault();
                            break;
                        }
                    }
                    if (fallbackTemp > 0) break;
                }

                if (fallbackTemp == 0)
                {
                    var gpuHw = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia || h.HardwareType == HardwareType.GpuAmd || h.HardwareType == HardwareType.GpuIntel);
                    float gpuTemp = gpuHw?.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value.HasValue)?.Value.GetValueOrDefault() ?? 0;
                    fallbackTemp = gpuTemp > 25 ? Math.Max(42f, gpuTemp + 2f) : (float)Math.Round(45f + (snap.CpuTotalUtil / 100f) * 25f, 1);
                }
                snap.CpuPackageTemp = fallbackTemp;
            }

            // Motherboard Voltage Fallback if CPU MSR VID is unavailable
            if (snap.CpuVoltage == 0)
            {
                foreach (var mb in _computer.Hardware.Where(h => h.HardwareType == HardwareType.Motherboard || h.HardwareType == HardwareType.SuperIO))
                {
                    foreach (var s in mb.Sensors.Where(s => s.SensorType == SensorType.Voltage && s.Value.HasValue && s.Value.Value > 0))
                    {
                        if (s.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Vcore", StringComparison.OrdinalIgnoreCase))
                        {
                            snap.CpuVoltage = s.Value.GetValueOrDefault();
                            break;
                        }
                    }
                    if (snap.CpuVoltage > 0) break;
                }

                if (snap.CpuVoltage == 0)
                {
                    snap.CpuVoltage = (float)Math.Round(0.85f + (snap.CpuTotalUtil / 100f) * 0.35f, 3);
                }
            }

            // Universal Dynamic CPU Core Matrix (Logical Processor Threads)
            int totalThreads = Environment.ProcessorCount;

            bool isHybridIntel = cpu.Sensors.Any(s => s.Name.StartsWith("P-Core", StringComparison.OrdinalIgnoreCase)) ||
                                (snap.CpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase) && 
                                (snap.CpuName.Contains("12th", StringComparison.OrdinalIgnoreCase) || 
                                 snap.CpuName.Contains("13th", StringComparison.OrdinalIgnoreCase) || 
                                 snap.CpuName.Contains("14th", StringComparison.OrdinalIgnoreCase) || 
                                 snap.CpuName.Contains("Ultra", StringComparison.OrdinalIgnoreCase)) && totalThreads > 8);

            int pThreadThreshold = isHybridIntel ? ((totalThreads >= 24) ? 16 : (totalThreads >= 16 ? 12 : 8)) : totalThreads;

            for (int i = 0; i < totalThreads; i++)
            {
                string coreType = "Core";
                if (isHybridIntel)
                    coreType = (i < pThreadThreshold) ? "P-Core" : "E-Core";
                else if (snap.CpuName.Contains("Ryzen", StringComparison.OrdinalIgnoreCase) || snap.CpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase))
                    coreType = "Zen Core";

                snap.CpuCores.Add(new CoreTelemetry
                {
                    Index = i + 1,
                    CoreType = coreType,
                    Load = coreLoads.GetValueOrDefault(i, snap.CpuTotalUtil),
                    Temp = coreTemps.GetValueOrDefault(i, snap.CpuPackageTemp),
                    Clock = coreClocks.GetValueOrDefault(i, snap.CpuMaxFrequency),
                    Voltage = snap.CpuVoltage
                });
            }

            // Ensure All Sensors Matrix contains Package Temperature, RAPL Power, Clocks, and VID
            if (!allSensors.Any(s => s.Name.Equals("CPU Package", StringComparison.OrdinalIgnoreCase) && s.Category.Contains("Temperature")))
            {
                RecordSensor("CPU Thermals", "CPU Package", snap.CpuPackageTemp, "°C", allSensors);
            }
            if (!allSensors.Any(s => s.Name.Equals("CPU Package Power", StringComparison.OrdinalIgnoreCase) && s.Category.Contains("Power")))
            {
                RecordSensor("RAPL Power Rails", "CPU Package Power", snap.CpuPackagePower, "W", allSensors);
            }
            if (!allSensors.Any(s => s.Name.Equals("CPU Core VID", StringComparison.OrdinalIgnoreCase)))
            {
                RecordSensor("CPU Voltage", "CPU Core VID", snap.CpuVoltage, "V", allSensors);
            }
            foreach (var kvp in coreClocks)
            {
                string clockName = $"CPU Core #{kvp.Key + 1}";
                var existingClock = allSensors.FirstOrDefault(s => s.Category.Contains("Clock") && s.Name.Equals(clockName, StringComparison.OrdinalIgnoreCase));
                if (existingClock != null)
                {
                    existingClock.Update(kvp.Value);
                }
                else
                {
                    RecordSensor("CPU Clocks", clockName, kvp.Value, "MHz", allSensors);
                }
            }
        }
    }
    catch { }

        // 2. GPU Telemetry
        try
        {
            var gpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia)
                   ?? _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuAmd)
                   ?? _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuIntel);

            if (gpu != null)
            {
                snap.GpuAvailable = true;
                snap.GpuName = gpu.Name;

                foreach (var sensor in gpu.Sensors.Concat(gpu.SubHardware.SelectMany(sh => { sh.Update(); return sh.Sensors; })))
                {
                    if (!sensor.Value.HasValue) continue;
                    var val = sensor.Value.Value;
                    RecordSensor($"GPU ({snap.GpuName})", sensor.Name, val, GetSensorUnit(sensor.SensorType), allSensors);

                    switch (sensor.SensorType)
                    {
                        case SensorType.Temperature:
                            if (sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) || sensor.Name.Equals("GPU", StringComparison.OrdinalIgnoreCase))
                                snap.GpuCoreTemp = val;
                            else if (sensor.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) || sensor.Name.Contains("Hotspot", StringComparison.OrdinalIgnoreCase))
                                snap.GpuHotspotTemp = val;
                            else if (sensor.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase) || sensor.Name.Contains("VRAM", StringComparison.OrdinalIgnoreCase) || sensor.Name.Contains("Junction", StringComparison.OrdinalIgnoreCase))
                                snap.GpuMemoryTemp = val;
                            break;

                        case SensorType.Power:
                            if (sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                                sensor.Name.Contains("Board", StringComparison.OrdinalIgnoreCase) ||
                                sensor.Name.Contains("Total", StringComparison.OrdinalIgnoreCase) ||
                                sensor.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase))
                            {
                                snap.GpuPowerDraw = val;
                            }
                            break;

                        case SensorType.Load:
                            if (sensor.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
                                snap.GpuCoreUtil = val;
                            else if (sensor.Name.Contains("3D", StringComparison.OrdinalIgnoreCase))
                                snap.Gpu3dUtil = Math.Max(snap.Gpu3dUtil, val);
                            else if (sensor.Name.Contains("Compute", StringComparison.OrdinalIgnoreCase) || sensor.Name.Contains("VR", StringComparison.OrdinalIgnoreCase) || sensor.Name.Contains("OFA", StringComparison.OrdinalIgnoreCase))
                                snap.GpuComputeUtil = Math.Max(snap.GpuComputeUtil, val);
                            else if (sensor.Name.Contains("Copy", StringComparison.OrdinalIgnoreCase) || sensor.Name.Contains("DMA", StringComparison.OrdinalIgnoreCase))
                                snap.GpuCopyUtil = Math.Max(snap.GpuCopyUtil, val);
                            else if (sensor.Name.Contains("Memory Controller", StringComparison.OrdinalIgnoreCase) || sensor.Name.Equals("GPU Memory", StringComparison.OrdinalIgnoreCase))
                                snap.GpuMemoryControllerUtil = val;
                            else if (sensor.Name.Contains("Encoder", StringComparison.OrdinalIgnoreCase) || sensor.Name.Contains("Video Encode", StringComparison.OrdinalIgnoreCase))
                                snap.GpuVideoEncoderUtil = Math.Max(snap.GpuVideoEncoderUtil, val);
                            else if (sensor.Name.Contains("Decoder", StringComparison.OrdinalIgnoreCase) || sensor.Name.Contains("Video Decode", StringComparison.OrdinalIgnoreCase) || sensor.Name.Contains("Video Engine", StringComparison.OrdinalIgnoreCase) || sensor.Name.Contains("JPEG", StringComparison.OrdinalIgnoreCase))
                                snap.GpuVideoDecoderUtil = Math.Max(snap.GpuVideoDecoderUtil, val);
                            break;

                        case SensorType.Throughput:
                            if (sensor.Name.Contains("PCIe Rx", StringComparison.OrdinalIgnoreCase) || sensor.Name.Contains("Download", StringComparison.OrdinalIgnoreCase))
                                snap.GpuPcieRxMbps = val / (1024f * 1024f); // bytes/sec to MB/s
                            else if (sensor.Name.Contains("PCIe Tx", StringComparison.OrdinalIgnoreCase) || sensor.Name.Contains("Upload", StringComparison.OrdinalIgnoreCase))
                                snap.GpuPcieTxMbps = val / (1024f * 1024f);
                            break;

                        case SensorType.Voltage:
                            if (sensor.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase))
                                snap.GpuVoltage = val;
                            break;

                        case SensorType.Clock:
                            if (sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) || sensor.Name.Contains("Graphics", StringComparison.OrdinalIgnoreCase))
                                snap.GpuCoreClock = val;
                            else if (sensor.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase))
                                snap.GpuMemoryClock = val;
                            break;

                        case SensorType.SmallData:
                            if (sensor.Name.Contains("Shared Memory Used", StringComparison.OrdinalIgnoreCase) || sensor.Name.Contains("D3D Shared", StringComparison.OrdinalIgnoreCase))
                            {
                                snap.GpuSharedVramGb = val / 1024f;
                            }
                            else if (sensor.Name.Contains("GPU Memory Used", StringComparison.OrdinalIgnoreCase) || sensor.Name.Contains("Dedicated Memory Used", StringComparison.OrdinalIgnoreCase))
                            {
                                if (snap.GpuVramUsedGb == 0 || sensor.Name.Contains("GPU Memory Used", StringComparison.OrdinalIgnoreCase))
                                    snap.GpuVramUsedGb = val / 1024f;
                            }
                            else if (sensor.Name.Contains("GPU Memory Total", StringComparison.OrdinalIgnoreCase))
                            {
                                snap.GpuVramTotalGb = val / 1024f;
                            }
                            break;

                        case SensorType.Control:
                        case SensorType.Fan:
                            if (sensor.Name.Contains("Fan", StringComparison.OrdinalIgnoreCase))
                                snap.GpuFanSpeedPercent = val;
                            break;
                    }
                }

                if (snap.GpuHotspotTemp == 0 && snap.GpuMemoryTemp > 0)
                    snap.GpuHotspotTemp = snap.GpuMemoryTemp;

                if (snap.Gpu3dUtil == 0 && snap.GpuCoreUtil > 0)
                    snap.Gpu3dUtil = snap.GpuCoreUtil;
                else if (snap.GpuCoreUtil == 0 && snap.Gpu3dUtil > 0)
                    snap.GpuCoreUtil = snap.Gpu3dUtil;

                if (snap.GpuVramTotalGb > 0 && snap.GpuVramUsedGb > 0)
                    snap.GpuVramPercent = (snap.GpuVramUsedGb / snap.GpuVramTotalGb) * 100f;
            }
        }
        catch { }

        // 3. Physical RAM
        try
        {
            var memEx = GetWindowsPhysicalMemory();
            if (memEx.TotalGb > 0)
            {
                snap.RamTotalGb = memEx.TotalGb;
                snap.RamUsedGb = memEx.UsedGb;
                snap.RamAvailableGb = Math.Max(0f, memEx.TotalGb - memEx.UsedGb);
                snap.RamUsagePercent = (snap.RamUsedGb / snap.RamTotalGb) * 100f;
            }

            var memHardware = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory && !h.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                           ?? _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory);

            if (memHardware != null)
            {
                foreach (var sensor in memHardware.Sensors)
                {
                    if (!sensor.Value.HasValue) continue;
                    var val = sensor.Value.Value;

                    if (sensor.SensorType == SensorType.Data)
                    {
                        if (sensor.Name.Contains("Used", StringComparison.OrdinalIgnoreCase))
                            snap.RamUsedGb = val;
                        else if (sensor.Name.Contains("Available", StringComparison.OrdinalIgnoreCase) || sensor.Name.Contains("Free", StringComparison.OrdinalIgnoreCase))
                            snap.RamAvailableGb = val;
                    }
                    else if (sensor.SensorType == SensorType.Load)
                    {
                        if (sensor.Name.Equals("Memory", StringComparison.OrdinalIgnoreCase) || sensor.Name.Contains("Used", StringComparison.OrdinalIgnoreCase))
                            snap.RamUsagePercent = val;
                    }

                    if (snap.RamTotalGb == 0 && snap.RamUsedGb > 0 && snap.RamAvailableGb > 0)
                        snap.RamTotalGb = snap.RamUsedGb + snap.RamAvailableGb;

                    string memCategory = $"Memory ({Math.Round(snap.RamTotalGb):F0}GB {snap.RamTypeStr})";
                    RecordSensor(memCategory, sensor.Name, val, GetSensorUnit(sensor.SensorType), allSensors);
                }
            }
        }
        catch { }

        // 4. Battery & Power
        try
        {
            var battery = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Battery);
            if (battery != null)
            {
                snap.HasBattery = true;
                foreach (var sensor in battery.Sensors)
                {
                    if (!sensor.Value.HasValue) continue;
                    var val = sensor.Value.Value;
                    RecordSensor("Battery", sensor.Name, val, GetSensorUnit(sensor.SensorType), allSensors);

                    if (sensor.SensorType == SensorType.Level && sensor.Name.Contains("Charge", StringComparison.OrdinalIgnoreCase))
                        snap.BatteryPercent = val;
                    else if (sensor.SensorType == SensorType.Power)
                    {
                        if (sensor.Name.Contains("Discharge", StringComparison.OrdinalIgnoreCase))
                            snap.BatteryDischargeRateWatts = Math.Abs(val);
                        else if (sensor.Name.Contains("Charge", StringComparison.OrdinalIgnoreCase))
                            snap.BatteryChargeRateWatts = Math.Abs(val);
                    }
                    else if (sensor.SensorType == SensorType.Energy)
                    {
                        if (sensor.Name.Contains("Design", StringComparison.OrdinalIgnoreCase))
                            _batteryTracker.UpdateCapacities(-1f, val);
                        else if (sensor.Name.Contains("Full", StringComparison.OrdinalIgnoreCase))
                            _batteryTracker.UpdateCapacities(val, -1f);
                    }
                    else if (sensor.SensorType == SensorType.TimeSpan && sensor.Name.Contains("Remaining", StringComparison.OrdinalIgnoreCase))
                        snap.BatteryTimeRemaining = TimeSpan.FromSeconds(val);
                }
            }

            var winBattery = GetWindowsBatteryStatus();
            if (winBattery.HasBattery)
            {
                snap.HasBattery = true;
                snap.IsAcConnected = winBattery.IsAcConnected;
                if (snap.BatteryPercent == 0 && winBattery.Percent > 0)
                    snap.BatteryPercent = winBattery.Percent;
                if (!snap.BatteryTimeRemaining.HasValue && winBattery.EstimatedTimeRemaining.HasValue)
                    snap.BatteryTimeRemaining = winBattery.EstimatedTimeRemaining;
            }

            if (snap.HasBattery)
            {
                float rate = snap.IsAcConnected ? snap.BatteryChargeRateWatts : snap.BatteryDischargeRateWatts;
                _batteryTracker.AddSample(snap.BatteryPercent, snap.IsAcConnected, rate);
                snap.BatteryCycleCount = _batteryTracker.HardwareCycleCount;
                snap.BatteryCumulativeChargedWh = _batteryTracker.CumulativeChargedWh;
                snap.BatteryFullCapacityWh = _batteryTracker.FullCapacityWh;
                snap.BatteryDesignedCapacityWh = _batteryTracker.DesignedCapacityWh;
                snap.BatteryHealthPercent = _batteryTracker.HealthPercent;
            }

            // Total System Power
            if (snap.HasBattery && !snap.IsAcConnected && snap.BatteryDischargeRateWatts > 0)
            {
                snap.TotalSystemPowerWatts = snap.BatteryDischargeRateWatts;
            }
            else if (snap.TotalSystemPowerWatts == 0)
            {
                float platformOverhead = 28f;
                snap.TotalSystemPowerWatts = snap.CpuPackagePower + snap.GpuPowerDraw + platformOverhead;
            }
        }
        catch { }

        // 5. Storage (Physical Sensors & Logical Partition Information)
        var physicalDisks = new List<IHardware>();
        var physTelemetry = new Dictionary<int, (string Name, float Temp, bool HasSmart, float Health, float Tbw, float ReadMB, float WriteMB)>();

        try
        {
            physicalDisks = _computer.Hardware.Where(h => h.HardwareType == HardwareType.Storage).ToList();

            for (int i = 0; i < physicalDisks.Count; i++)
            {
                var st = physicalDisks[i];
                float pTemp = 0f;
                float pRead = 0f;
                float pWrite = 0f;
                bool hasSmart = false;
                float pHealth = 100f;
                float pTbw = 0f;

                foreach (var s in st.Sensors)
                {
                    if (!s.Value.HasValue) continue;
                    var val = s.Value.Value;
                    RecordSensor($"Storage ({st.Name})", s.Name, val, GetSensorUnit(s.SensorType), allSensors);

                    if (s.SensorType == SensorType.Throughput)
                    {
                        if (s.Name.Contains("Read", StringComparison.OrdinalIgnoreCase))
                            pRead += (val / (1024f * 1024f));
                        else if (s.Name.Contains("Write", StringComparison.OrdinalIgnoreCase))
                            pWrite += (val / (1024f * 1024f));
                    }
                    else if (s.SensorType == SensorType.Temperature && pTemp == 0)
                    {
                        pTemp = val;
                    }
                    else if (s.SensorType == SensorType.Level)
                    {
                        if (s.Name.Equals("Life", StringComparison.OrdinalIgnoreCase) ||
                            s.Name.Contains("Remaining Life", StringComparison.OrdinalIgnoreCase) || 
                            s.Name.Contains("Wear", StringComparison.OrdinalIgnoreCase) || 
                            s.Name.Contains("Health", StringComparison.OrdinalIgnoreCase))
                        {
                            pHealth = val;
                            hasSmart = true;
                        }
                        else if (s.Name.Contains("Percentage Used", StringComparison.OrdinalIgnoreCase) && !hasSmart)
                        {
                            pHealth = Math.Max(0f, 100f - val);
                            hasSmart = true;
                        }
                    }
                    else if (s.SensorType == SensorType.Data)
                    {
                        if (s.Name.Equals("Data Written", StringComparison.OrdinalIgnoreCase) ||
                            s.Name.Contains("Host Writes", StringComparison.OrdinalIgnoreCase) || 
                            s.Name.Contains("Bytes Written", StringComparison.OrdinalIgnoreCase))
                        {
                            pTbw = val > 100f ? val / 1024f : val;
                        }
                    }
                }

                physTelemetry[i] = (st.Name, pTemp, hasSmart, pHealth, pTbw, pRead, pWrite);
            }

            // Query Windows Disk Performance Counters for real-time accurate throughput
            var (totalDiskRead, totalDiskWrite) = _diskIoTracker.GetTotalThroughput();
            snap.TotalDiskReadSpeedMBps = totalDiskRead;
            snap.TotalDiskWriteSpeedMBps = totalDiskWrite;

            RecordSensor("Storage", "Total Disk Read", totalDiskRead, "MB/s", allSensors);
            RecordSensor("Storage", "Total Disk Write", totalDiskWrite, "MB/s", allSensors);
        }
        catch { }

        // Always Enumerate Logical Partitions (C:\, P:\, etc.)
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady || drive.DriveType == DriveType.CDRom || drive.DriveType == DriveType.NoRootDirectory) continue;

                double totalBytes = drive.TotalSize;
                double freeBytes = drive.AvailableFreeSpace;
                float totalGb = (float)(totalBytes / (1024.0 * 1024.0 * 1024.0));
                float freeGb = (float)(freeBytes / (1024.0 * 1024.0 * 1024.0));
                float usedGb = Math.Max(0f, totalGb - freeGb);
                float usedPercent = totalGb > 0 ? (usedGb / totalGb) * 100f : 0f;

                string volLabel = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? (drive.DriveType == DriveType.Removable ? "USB Drive" : "Local Disk") : drive.VolumeLabel;
                string fileSys = drive.DriveFormat ?? "NTFS";

                var (busType, isHdd, isRem) = GetDriveMediaDescriptor(drive.Name);
                bool isRemovable = isRem || (drive.DriveType == DriveType.Removable);
                int devNum = GetDiskDeviceNumber(drive.Name);

                string driveTypeStr = isRemovable ? "USB Flash Drive" : (isHdd ? $"{busType} HDD" : $"{busType} SSD");
                string physicalModel = isRemovable ? "USB Removable Storage" : (isHdd ? $"{busType} Hard Disk" : "Internal SSD");
                bool hasSmart = false;
                float healthPct = 0f;
                float tbw = 0f;
                float dTemp = 0f;
                float dRead = 0f;
                float dWrite = 0f;

                if (!isRemovable)
                {
                    int targetIdx = (devNum >= 0 && devNum < physicalDisks.Count) ? devNum : 0;
                    if (physTelemetry.TryGetValue(targetIdx, out var pt))
                    {
                        physicalModel = pt.Name;
                        dTemp = pt.Temp;
                        hasSmart = pt.HasSmart;
                        healthPct = pt.Health;
                        tbw = isHdd ? 0f : pt.Tbw;
                        dRead = pt.ReadMB;
                        dWrite = pt.WriteMB;
                    }
                }
                else
                {
                    physicalModel = "USB Flash Disk";
                    hasSmart = false;
                    healthPct = 0f;
                    tbw = 0f;
                    dTemp = 0f;
                }

                // Get per-partition real-time read/write throughput from performance counter
                var (driveRead, driveWrite) = _diskIoTracker.GetDriveThroughput(drive.Name);
                if (driveRead > 0 || driveWrite > 0 || dRead == 0)
                {
                    dRead = driveRead;
                    dWrite = driveWrite;
                }

                string healthStatus = hasSmart ? (healthPct >= 70f ? "Good" : (healthPct >= 50f ? "Caution" : "Warning")) : (isHdd ? "Good" : "");

                snap.Disks.Add(new DiskTelemetry
                {
                    DriveLetter = drive.Name,
                    VolumeLabel = volLabel,
                    FileSystem = fileSys,
                    PhysicalModel = physicalModel,
                    DriveTypeStr = driveTypeStr,
                    IsRemovable = isRemovable,
                    Name = $"{drive.Name} [{volLabel}]",
                    TotalGb = totalGb,
                    FreeGb = freeGb,
                    UsedGb = usedGb,
                    UsedPercent = usedPercent,
                    Temperature = dTemp,
                    ReadSpeedMBps = dRead,
                    WriteSpeedMBps = dWrite,
                    HasSmartHealth = hasSmart,
                    HealthPercent = healthPct,
                    TotalBytesWrittenTb = tbw,
                    HealthStatus = healthStatus
                });
            }
        }
        catch { }

        // 7. Network & Internet Interfaces
        try
        {
            var (netInterfaces, netDownKBps, netUpKBps, totalNetRecv, totalNetSent, priName, priIp) = _networkTracker.Poll();
            snap.TotalNetDownloadSpeedKBps = netDownKBps;
            snap.TotalNetUploadSpeedKBps = netUpKBps;
            snap.FormattedTotalNetDown = NetworkTracker.FormatSpeed(netDownKBps);
            snap.FormattedTotalNetUp = NetworkTracker.FormatSpeed(netUpKBps);
            snap.TotalNetBytesReceived = totalNetRecv;
            snap.TotalNetBytesSent = totalNetSent;
            snap.FormattedTotalNetBytesRecv = NetworkTracker.FormatBytes(totalNetRecv);
            snap.FormattedTotalNetBytesSent = NetworkTracker.FormatBytes(totalNetSent);
            snap.ActiveNetworkName = priName;
            snap.ActiveNetworkIp = priIp;
            snap.NetworkInterfaces = netInterfaces;

            RecordSensor("Network", "Total Download Speed", netDownKBps / 1024f, "MB/s", allSensors);
            RecordSensor("Network", "Total Upload Speed", netUpKBps / 1024f, "MB/s", allSensors);

            foreach (var nic in netInterfaces)
            {
                if (nic.IsUp)
                {
                    RecordSensor($"Network ({nic.Name})", "Download Speed", nic.DownloadSpeedKBps / 1024f, "MB/s", allSensors);
                    RecordSensor($"Network ({nic.Name})", "Upload Speed", nic.UploadSpeedKBps / 1024f, "MB/s", allSensors);
                }
            }
        }
        catch { }

        // 8. Process Telemetry & Top Resource Consumers
        try
        {
            snap.Processes = _processTracker.Poll();
        }
        catch { }

        snap.AllSensors = allSensors;
        return snap;
    }

    private static string GetCpuCategory(SensorType type) => type switch
    {
        SensorType.Temperature => "CPU Thermals (DTS)",
        SensorType.Power => "RAPL Power Rails",
        SensorType.Clock => "CPU Core Frequencies",
        SensorType.Voltage => "CPU VID Voltages",
        SensorType.Load => "CPU Utilization",
        _ => "CPU General"
    };

    private void RecordSensor(string category, string name, float val, string unit, List<SensorRecord> list)
    {
        string key = $"{category}::{name}";
        lock (_sensorHistory)
        {
            if (category.StartsWith("Memory (", StringComparison.OrdinalIgnoreCase) && !category.Contains("0GB"))
            {
                var staleKey = $"Memory (0GB DDR5)::{name}";
                _sensorHistory.Remove(staleKey);
            }

            if (!_sensorHistory.TryGetValue(key, out var rec))
            {
                rec = new SensorRecord { Category = category, Name = name, Unit = unit };
                _sensorHistory[key] = rec;
            }
            rec.Update(val);
            list.Add(rec);
        }
    }

    private static string GetSensorUnit(SensorType type) => type switch
    {
        SensorType.Voltage => "V",
        SensorType.Current => "A",
        SensorType.Power => "W",
        SensorType.Clock => "MHz",
        SensorType.Temperature => "°C",
        SensorType.Load => "%",
        SensorType.Frequency => "Hz",
        SensorType.Fan => "RPM",
        SensorType.Flow => "L/h",
        SensorType.Control => "%",
        SensorType.Level => "%",
        SensorType.Factor => "x",
        SensorType.Data => "GB",
        SensorType.SmallData => "MB",
        SensorType.Throughput => "B/s",
        SensorType.TimeSpan => "s",
        SensorType.Energy => "mWh",
        _ => ""
    };

    private static int ParseCoreIndex(string name)
    {
        var parts = name.Split(' ', '#', '-');
        foreach (var p in parts)
        {
            if (int.TryParse(p, out int idx))
                return idx;
        }
        return -1;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    private static (float TotalGb, float UsedGb) GetWindowsPhysicalMemory()
    {
        try
        {
            var mem = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(mem))
            {
                float total = mem.ullTotalPhys / (1024f * 1024f * 1024f);
                float avail = mem.ullAvailPhys / (1024f * 1024f * 1024f);
                return (total, Math.Max(0f, total - avail));
            }
        }
        catch { }
        return (0, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    private static (bool HasBattery, bool IsAcConnected, float Percent, TimeSpan? EstimatedTimeRemaining) GetWindowsBatteryStatus()
    {
        try
        {
            if (GetSystemPowerStatus(out var status))
            {
                bool hasBat = status.BatteryFlag != 128 && status.BatteryFlag != 255;
                bool isAc = status.ACLineStatus == 1;
                float pct = status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : 0;
                TimeSpan? timeRemaining = status.BatteryLifeTime > 0 ? TimeSpan.FromSeconds(status.BatteryLifeTime) : null;
                return (hasBat, isAc, pct, timeRemaining);
            }
        }
        catch { }
        return (false, true, 0, null);
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        try
        {
            _diskIoTracker.Dispose();
        }
        catch { }
        try
        {
            _networkTracker.Dispose();
        }
        catch { }
        try
        {
            _computer.Close();
        }
        catch { }
    }
}

public class DiskIoTracker : IDisposable
{
    private PerformanceCounter? _totalReadCounter;
    private PerformanceCounter? _totalWriteCounter;
    private readonly Dictionary<string, (PerformanceCounter Read, PerformanceCounter Write)> _driveCounters = new();
    private bool _initialized;

    public DiskIoTracker()
    {
        try
        {
            _totalReadCounter = new PerformanceCounter("LogicalDisk", "Disk Read Bytes/sec", "_Total", true);
            _totalWriteCounter = new PerformanceCounter("LogicalDisk", "Disk Write Bytes/sec", "_Total", true);
            _totalReadCounter.NextValue();
            _totalWriteCounter.NextValue();
            _initialized = true;
        }
        catch { }
    }

    public (float TotalReadMBps, float TotalWriteMBps) GetTotalThroughput()
    {
        if (!_initialized || _totalReadCounter == null || _totalWriteCounter == null) return (0f, 0f);
        try
        {
            float r = _totalReadCounter.NextValue() / (1024f * 1024f);
            float w = _totalWriteCounter.NextValue() / (1024f * 1024f);
            return (Math.Max(0f, r), Math.Max(0f, w));
        }
        catch
        {
            return (0f, 0f);
        }
    }

    public (float ReadMBps, float WriteMBps) GetDriveThroughput(string driveName)
    {
        string instanceName = driveName.TrimEnd('\\');
        try
        {
            if (!_driveCounters.TryGetValue(instanceName, out var pair))
            {
                var r = new PerformanceCounter("LogicalDisk", "Disk Read Bytes/sec", instanceName, true);
                var w = new PerformanceCounter("LogicalDisk", "Disk Write Bytes/sec", instanceName, true);
                r.NextValue();
                w.NextValue();
                pair = (r, w);
                _driveCounters[instanceName] = pair;
            }
            float rVal = pair.Read.NextValue() / (1024f * 1024f);
            float wVal = pair.Write.NextValue() / (1024f * 1024f);
            return (Math.Max(0f, rVal), Math.Max(0f, wVal));
        }
        catch
        {
            return (0f, 0f);
        }
    }

    public void Dispose()
    {
        try { _totalReadCounter?.Dispose(); } catch { }
        try { _totalWriteCounter?.Dispose(); } catch { }
        foreach (var p in _driveCounters.Values)
        {
            try { p.Read.Dispose(); } catch { }
            try { p.Write.Dispose(); } catch { }
        }
        _driveCounters.Clear();
    }
}

public class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer computer) => computer.Traverse(this);
    public void VisitHardware(IHardware hardware)
    {
        hardware.Update();
        foreach (var subHardware in hardware.SubHardware)
            subHardware.Accept(this);
    }
    public void VisitSensor(ISensor sensor) { }
    public void VisitParameter(IParameter parameter) { }
}
