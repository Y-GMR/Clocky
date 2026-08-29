using System;
using System.Collections.Generic;

namespace Clocky.Core;

public class SensorRecord
{
    public string Category { get; set; } = "";
    public string Group { get; set; } = "";
    public string Name { get; set; } = "";
    public float Value { get; set; }
    public float Min { get; set; }
    public float Max { get; set; }
    public float Avg => Count > 0 ? Sum / Count : Value;
    public string Unit { get; set; } = "";

    public string FormattedCurrent => FormatValue(Value);
    public string FormattedMin => FormatValue(Min);
    public string FormattedMax => FormatValue(Max);
    public string FormattedAvg => FormatValue(Avg);

    public float Sum { get; set; }
    public int Count { get; set; }

    public void Update(float val)
    {
        Value = val;
        if (val < Min || Count == 0) Min = val;
        if (val > Max || Count == 0) Max = val;
        Sum += val;
        Count++;
    }

    public void Reset(float val = 0f)
    {
        Value = val;
        Min = val;
        Max = val;
        Sum = val;
        Count = 1;
    }

    private string FormatValue(float v)
    {
        if (Unit == "V") return v.ToString("F3");
        if (Unit == "W" || Unit == "°C" || Unit == "%" || Unit == "GB" || Unit == "MB" || Unit == "B/s") return v.ToString("F1");
        if (Unit == "MHz" || Unit == "RPM" || Unit == "Hz") return v.ToString("F0");
        return v.ToString("F1");
    }
}

public class CoreTelemetry
{
    public int Index { get; set; }
    public string CoreType { get; set; } = "Core";
    public float Load { get; set; }
    public float Temp { get; set; }
    public float Clock { get; set; }
    public float Voltage { get; set; }
}

public class DiskTelemetry
{
    public string DriveLetter { get; set; } = "";
    public string VolumeLabel { get; set; } = "";
    public string FileSystem { get; set; } = "NTFS";
    public string PhysicalModel { get; set; } = "";
    public string DriveTypeStr { get; set; } = "Fixed";
    public bool IsRemovable { get; set; } = false;
    public string Name { get; set; } = "";
    public bool HasSmartHealth { get; set; } = false;
    public float HealthPercent { get; set; } = 0f;
    public float TotalBytesWrittenTb { get; set; } = 0f;
    public string HealthStatus { get; set; } = "";
    public float Temperature { get; set; } = 0f;
    public float TotalGb { get; set; }
    public float FreeGb { get; set; }
    public float UsedGb { get; set; }
    public float UsedPercent { get; set; }
    public float ReadSpeedMBps { get; set; }
    public float WriteSpeedMBps { get; set; }
}

public class TelemetrySnapshot
{
    public DateTime Timestamp { get; set; } = DateTime.Now;

    // System
    public string SystemModelName { get; set; } = "Windows PC";

    // CPU
    public string CpuName { get; set; } = "CPU";
    public float CpuTotalUtil { get; set; }
    public float CpuPackageTemp { get; set; }
    public float CpuPackagePower { get; set; }
    public float CpuMaxFrequency { get; set; }
    public float CpuVoltage { get; set; }
    public List<CoreTelemetry> CpuCores { get; set; } = new();

    // GPU
    public bool GpuAvailable { get; set; }
    public string GpuName { get; set; } = "GPU";
    public float GpuCoreTemp { get; set; }
    public float GpuHotspotTemp { get; set; }
    public float GpuMemoryTemp { get; set; }
    public float GpuPowerDraw { get; set; }
    public float GpuCoreUtil { get; set; }
    public float Gpu3dUtil { get; set; }
    public float GpuComputeUtil { get; set; }
    public float GpuCopyUtil { get; set; }
    public float GpuMemoryControllerUtil { get; set; }
    public float GpuVideoEncoderUtil { get; set; }
    public float GpuVideoDecoderUtil { get; set; }
    public float GpuPcieRxMbps { get; set; }
    public float GpuPcieTxMbps { get; set; }
    public float GpuSharedVramGb { get; set; }
    public float GpuVoltage { get; set; }
    public float GpuCoreClock { get; set; }
    public float GpuMemoryClock { get; set; }
    public float GpuVramUsedGb { get; set; }
    public float GpuVramTotalGb { get; set; }
    public float GpuVramPercent { get; set; }
    public float GpuFanSpeedPercent { get; set; }

    // Memory
    public string RamTypeStr { get; set; } = "DDR5";
    public int RamSpeedMt { get; set; } = 5600;
    public float RamUsedGb { get; set; }
    public float RamAvailableGb { get; set; }
    public float RamTotalGb { get; set; }
    public float RamUsagePercent { get; set; }

    // Power & Battery
    public float TotalSystemPowerWatts { get; set; }
    public bool HasBattery { get; set; }
    public bool IsAcConnected { get; set; } = true;
    public float BatteryPercent { get; set; }
    public float BatteryDischargeRateWatts { get; set; }
    public float BatteryChargeRateWatts { get; set; }
    public TimeSpan? BatteryTimeRemaining { get; set; }
    public int BatteryCycleCount { get; set; }
    public float BatteryCumulativeChargedWh { get; set; }
    public float BatteryFullCapacityWh { get; set; } = 0f;
    public float BatteryDesignedCapacityWh { get; set; } = 0f;
    public float BatteryHealthPercent { get; set; } = 100f;

    // Storage
    public float TotalDiskReadSpeedMBps { get; set; }
    public float TotalDiskWriteSpeedMBps { get; set; }
    public List<DiskTelemetry> Disks { get; set; } = new();

    // Network & Internet Interfaces
    public float TotalNetDownloadSpeedKBps { get; set; }
    public float TotalNetUploadSpeedKBps { get; set; }
    public string FormattedTotalNetDown { get; set; } = "0.0 KB/s";
    public string FormattedTotalNetUp { get; set; } = "0.0 KB/s";
    public ulong TotalNetBytesReceived { get; set; }
    public ulong TotalNetBytesSent { get; set; }
    public string FormattedTotalNetBytesRecv { get; set; } = "0.0 GB";
    public string FormattedTotalNetBytesSent { get; set; } = "0.0 GB";
    public string ActiveNetworkName { get; set; } = "No Network";
    public string ActiveNetworkIp { get; set; } = "";
    public List<NetworkInterfaceTelemetry> NetworkInterfaces { get; set; } = new();

    // Process Telemetry & Top Resource Consumers
    public ProcessTelemetrySnapshot Processes { get; set; } = new();

    // Raw Tabular Sensor List
    public List<SensorRecord> AllSensors { get; set; } = new();
}

public class ProcessItem
{
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    public int InstanceCount { get; set; } = 1;
    public string DisplayName => InstanceCount > 1 ? $"{Name} ({InstanceCount})" : Name;
    public string FormattedPid => InstanceCount > 1 ? $"{Pid} (+{InstanceCount - 1})" : Pid.ToString();

    public float CpuPercent { get; set; }
    public string FormattedCpu => $"{CpuPercent:F1}%";

    public float GpuPercent { get; set; }
    public string FormattedGpu => $"{GpuPercent:F1}%";
    public float GpuVramMb { get; set; }
    public string FormattedGpuVram => GpuVramMb >= 1024f ? $"{(GpuVramMb / 1024f):F1} GB" : $"{GpuVramMb:F0} MB";

    public long PrivateMemoryBytes { get; set; }
    public float PrivateMemoryMb => PrivateMemoryBytes / (1024f * 1024f);
    public string FormattedPrivateMemory => PrivateMemoryMb >= 1024f ? $"{(PrivateMemoryMb / 1024f):F1} GB" : $"{PrivateMemoryMb:F0} MB";

    public long WorkingSetBytes { get; set; }
    public float WorkingSetMb => WorkingSetBytes / (1024f * 1024f);
    public string FormattedWorkingSet => WorkingSetMb >= 1024f ? $"{(WorkingSetMb / 1024f):F1} GB" : $"{WorkingSetMb:F0} MB";

    public float NetDownSpeedKBps { get; set; }
    public string FormattedNetDown => NetworkTracker.FormatSpeed(NetDownSpeedKBps);

    public float NetUpSpeedKBps { get; set; }
    public string FormattedNetUp => NetworkTracker.FormatSpeed(NetUpSpeedKBps);

    public int ThreadCount { get; set; }
    public string Status { get; set; } = "Running";
}

public class ProcessTelemetrySnapshot
{
    public List<ProcessItem> TopCpu { get; set; } = new();
    public List<ProcessItem> TopGpu { get; set; } = new();
    public List<ProcessItem> TopRam { get; set; } = new();
    public List<ProcessItem> TopNetDown { get; set; } = new();
    public List<ProcessItem> TopNetUp { get; set; } = new();
    public List<ProcessItem> AllProcesses { get; set; } = new();
}

public class NetworkInterfaceTelemetry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string InterfaceType { get; set; } = "Network Adapter";
    public bool IsUp { get; set; }
    public string Status { get; set; } = "Disconnected";
    public float SpeedMbps { get; set; }
    public string SpeedFormatted { get; set; } = "";
    public string Ipv4Address { get; set; } = "";
    public string Ipv6Address { get; set; } = "";
    public string Gateway { get; set; } = "";
    public string Dns { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public float DownloadSpeedKBps { get; set; }
    public float UploadSpeedKBps { get; set; }
    public string DownloadSpeedFormatted { get; set; } = "0.0 KB/s";
    public string UploadSpeedFormatted { get; set; } = "0.0 KB/s";
    public ulong TotalBytesReceived { get; set; }
    public ulong TotalBytesSent { get; set; }
    public string FormattedTotalReceived { get; set; } = "0.0 MB";
    public string FormattedTotalSent { get; set; } = "0.0 MB";
    public List<float> DownloadHistory { get; set; } = new();
    public List<float> UploadHistory { get; set; } = new();
}
