using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Clocky.Core;

public class SensorRecord
{
    private readonly Queue<float> _recentSamples = new(64);
    private double _sumDouble;
    private double _windowSum;

    public string Category { get; set; } = "";
    public string Group { get; set; } = "";
    public string Name { get; set; } = "";
    public float Value { get; set; }
    public float Min { get; set; }
    public float Max { get; set; }
    public float Avg { get; private set; }
    public string Unit { get; set; } = "";
    public SensorProvenance Provenance { get; set; } = SensorProvenance.DriverLHM;
    public string ProvenanceLabel => Provenance switch
    {
        SensorProvenance.NativeMSR => "MSR",
        SensorProvenance.DriverLHM => "LHM",
        SensorProvenance.DriverNVML => "NVML",
        SensorProvenance.MotherboardSuperIO => "SuperIO",
        SensorProvenance.KernelETW => "ETW",
        SensorProvenance.PerformanceCounter => "PerfCtr",
        SensorProvenance.FallbackApproximation => "Fallback",
        _ => "—"
    };

    public string FormattedCurrent => FormatValue(Value);
    public string FormattedMin => FormatValue(Min);
    public string FormattedMax => FormatValue(Max);
    public string FormattedAvg => FormatValue(Avg);

    public float Sum => (float)_sumDouble;
    public int Count { get; private set; }

    public void Update(float val)
    {
        Value = val;
        if (val < Min || Count == 0) Min = val;
        if (val > Max || Count == 0) Max = val;
        
        _sumDouble += val;
        Count++;

        _recentSamples.Enqueue(val);
        _windowSum += val;
        while (_recentSamples.Count > 60)
        {
            _windowSum -= _recentSamples.Dequeue();
        }

        Avg = _recentSamples.Count > 0 ? (float)(_windowSum / _recentSamples.Count) : val;
    }

    public void Reset(float val = 0f)
    {
        Value = val;
        Min = val;
        Max = val;
        Avg = val;
        _sumDouble = val;
        _windowSum = val;
        Count = 1;
        _recentSamples.Clear();
        _recentSamples.Enqueue(val);
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
    public int CoreNumber { get; set; }
    public int SmtThreadNumber { get; set; }
    public bool IsSmt { get; set; }
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
    public float CpuCoreMaxTemp { get; set; }
    public float CpuCoreAvgTemp { get; set; }
    public float CpuPackagePower { get; set; }
    public float CpuMaxFrequency { get; set; }
    public float CpuVoltage { get; set; }
    public SensorProvenance CpuPackageTempProvenance { get; set; } = SensorProvenance.Unavailable;
    public string CpuPackageTempProvenanceLabel => CpuPackageTempProvenance switch
    {
        SensorProvenance.NativeMSR => "Native MSR (PECI)",
        SensorProvenance.DriverLHM => "LHM Driver (Tdie)",
        SensorProvenance.MotherboardSuperIO => "Motherboard Super I/O",
        SensorProvenance.FallbackApproximation => "Core Max Fallback",
        _ => "Unavailable"
    };

    public SensorProvenance CpuVoltageProvenance { get; set; } = SensorProvenance.Unavailable;
    public string CpuVoltageProvenanceLabel => CpuVoltageProvenance switch
    {
        SensorProvenance.NativeMSR => "Native MSR (VID)",
        SensorProvenance.MotherboardSuperIO => "Motherboard Vcore",
        _ => "Unavailable"
    };

    public bool CpuVoltageIsVid
    {
        get => CpuVoltageProvenance == SensorProvenance.NativeMSR;
        set => CpuVoltageProvenance = value ? SensorProvenance.NativeMSR : SensorProvenance.MotherboardSuperIO;
    }
    public GpuProcessEngineState GpuProcessEngineState { get; set; } = GpuProcessEngineState.Active;
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
    public string RamTypeStr { get; set; } = "RAM";
    public int RamSpeedMt { get; set; } = 0;
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

public class ProcessItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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

    public float DiskReadMBps { get; set; }
    public string FormattedDiskRead => DiskReadMBps >= 1000f ? $"{(DiskReadMBps / 1024f):F1} GB/s" : (DiskReadMBps >= 0.05f ? $"{DiskReadMBps:F1} MB/s" : "0.0 MB/s");

    public float DiskWriteMBps { get; set; }
    public string FormattedDiskWrite => DiskWriteMBps >= 1000f ? $"{(DiskWriteMBps / 1024f):F1} GB/s" : (DiskWriteMBps >= 0.05f ? $"{DiskWriteMBps:F1} MB/s" : "0.0 MB/s");

    public string FormattedDiskIo => (DiskReadMBps > 0.05f || DiskWriteMBps > 0.05f) ? $"R: {DiskReadMBps:F1} • W: {DiskWriteMBps:F1} MB/s" : "0.0 MB/s";

    public int EstablishedSockets { get; set; }
    public int ActiveSockets { get; set; }
    public string FormattedSockets => ActiveSockets > 0 ? (EstablishedSockets > 0 ? $"{EstablishedSockets} Conns" : $"{ActiveSockets} Sockets") : "0";
    public string FormattedActiveNet => EstablishedSockets > 0 ? $"{EstablishedSockets} Conns" : (ActiveSockets > 0 ? $"{ActiveSockets} Sockets" : "Idle");

    public float NetDownSpeedKBps { get; set; }
    public string FormattedNetDown => NetworkTracker.FormatSpeed(NetDownSpeedKBps);

    public float NetUpSpeedKBps { get; set; }
    public string FormattedNetUp => NetworkTracker.FormatSpeed(NetUpSpeedKBps);

    public string FormattedNetThroughput => (NetDownSpeedKBps > 0.05f || NetUpSpeedKBps > 0.05f) ? $"↓ {NetworkTracker.FormatSpeed(NetDownSpeedKBps)} • ↑ {NetworkTracker.FormatSpeed(NetUpSpeedKBps)}" : (EstablishedSockets > 0 ? $"{EstablishedSockets} Conns" : (ActiveSockets > 0 ? $"{ActiveSockets} Sockets" : "0 B/s"));

    public int ThreadCount { get; set; }
    public string Status { get; set; } = "Running";

    public List<ProcessInstanceItem> Children { get; set; } = new();
    public ObservableCollection<ProcessInstanceItem> ObservableChildren { get; } = new();
    public bool HasChildren => Children.Count > 1;

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                if (_isExpanded)
                    SyncObservableChildren();
                else if (ObservableChildren.Count > 0)
                    ObservableChildren.Clear();
                OnPropertyChanged(nameof(IsExpanded));
                OnPropertyChanged(nameof(DetailsVisible));
            }
        }
    }

    public bool DetailsVisible => HasChildren && _isExpanded;

    public void UpdateFrom(ProcessItem src, bool isExpanded)
    {
        bool pidOrCountChanged = Pid != src.Pid || InstanceCount != src.InstanceCount;
        bool nameChanged = !string.Equals(Name, src.Name, StringComparison.Ordinal);
        bool oldHasChildren = HasChildren;

        Pid = src.Pid;
        Name = src.Name;
        InstanceCount = src.InstanceCount;
        Children = src.Children;

        if (pidOrCountChanged)
        {
            OnPropertyChanged(nameof(Pid));
            OnPropertyChanged(nameof(InstanceCount));
            OnPropertyChanged(nameof(FormattedPid));
        }
        if (pidOrCountChanged || nameChanged)
        {
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(DisplayName));
        }

        if (Math.Abs(CpuPercent - src.CpuPercent) >= 0.05f)
        {
            CpuPercent = src.CpuPercent;
            OnPropertyChanged(nameof(CpuPercent));
            OnPropertyChanged(nameof(FormattedCpu));
        }
        else
        {
            CpuPercent = src.CpuPercent;
        }

        if (Math.Abs(GpuPercent - src.GpuPercent) >= 0.05f || Math.Abs(GpuVramMb - src.GpuVramMb) >= 0.5f)
        {
            GpuPercent = src.GpuPercent;
            GpuVramMb = src.GpuVramMb;
            OnPropertyChanged(nameof(GpuPercent));
            OnPropertyChanged(nameof(FormattedGpu));
            OnPropertyChanged(nameof(GpuVramMb));
            OnPropertyChanged(nameof(FormattedGpuVram));
        }
        else
        {
            GpuPercent = src.GpuPercent;
            GpuVramMb = src.GpuVramMb;
        }

        if (PrivateMemoryBytes != src.PrivateMemoryBytes)
        {
            PrivateMemoryBytes = src.PrivateMemoryBytes;
            OnPropertyChanged(nameof(PrivateMemoryBytes));
            OnPropertyChanged(nameof(FormattedPrivateMemory));
        }

        if (WorkingSetBytes != src.WorkingSetBytes)
        {
            WorkingSetBytes = src.WorkingSetBytes;
            OnPropertyChanged(nameof(WorkingSetBytes));
            OnPropertyChanged(nameof(FormattedWorkingSet));
        }

        if (Math.Abs(DiskReadMBps - src.DiskReadMBps) >= 0.05f)
        {
            DiskReadMBps = src.DiskReadMBps;
            OnPropertyChanged(nameof(DiskReadMBps));
            OnPropertyChanged(nameof(FormattedDiskRead));
            OnPropertyChanged(nameof(FormattedDiskIo));
        }
        else
        {
            DiskReadMBps = src.DiskReadMBps;
        }

        if (Math.Abs(DiskWriteMBps - src.DiskWriteMBps) >= 0.05f)
        {
            DiskWriteMBps = src.DiskWriteMBps;
            OnPropertyChanged(nameof(DiskWriteMBps));
            OnPropertyChanged(nameof(FormattedDiskWrite));
            OnPropertyChanged(nameof(FormattedDiskIo));
        }
        else
        {
            DiskWriteMBps = src.DiskWriteMBps;
        }

        if (EstablishedSockets != src.EstablishedSockets || ActiveSockets != src.ActiveSockets)
        {
            EstablishedSockets = src.EstablishedSockets;
            ActiveSockets = src.ActiveSockets;
            OnPropertyChanged(nameof(EstablishedSockets));
            OnPropertyChanged(nameof(ActiveSockets));
            OnPropertyChanged(nameof(FormattedSockets));
            OnPropertyChanged(nameof(FormattedActiveNet));
        }

        NetDownSpeedKBps = src.NetDownSpeedKBps;
        NetUpSpeedKBps = src.NetUpSpeedKBps;

        if (ThreadCount != src.ThreadCount)
        {
            ThreadCount = src.ThreadCount;
            OnPropertyChanged(nameof(ThreadCount));
        }

        if (!string.Equals(Status, src.Status, StringComparison.Ordinal))
        {
            Status = src.Status;
            OnPropertyChanged(nameof(Status));
        }

        bool newHasChildren = HasChildren;
        bool effectiveExpanded = newHasChildren && isExpanded;
        if (_isExpanded != effectiveExpanded || oldHasChildren != newHasChildren)
        {
            _isExpanded = effectiveExpanded;
            if (_isExpanded)
                SyncObservableChildren();
            else if (ObservableChildren.Count > 0)
                ObservableChildren.Clear();
            OnPropertyChanged(nameof(HasChildren));
            OnPropertyChanged(nameof(IsExpanded));
            OnPropertyChanged(nameof(DetailsVisible));
        }
        else if (_isExpanded)
        {
            SyncObservableChildren();
        }
    }

    private void SyncObservableChildren()
    {
        if (!HasChildren)
        {
            if (ObservableChildren.Count > 0) ObservableChildren.Clear();
            return;
        }

        int targetCount = Children.Count;
        while (ObservableChildren.Count > targetCount)
        {
            ObservableChildren.RemoveAt(ObservableChildren.Count - 1);
        }

        for (int i = 0; i < targetCount; i++)
        {
            var srcChild = Children[i];
            if (i < ObservableChildren.Count)
            {
                ObservableChildren[i].UpdateFrom(srcChild);
            }
            else
            {
                var copy = new ProcessInstanceItem();
                copy.UpdateFrom(srcChild);
                ObservableChildren.Add(copy);
            }
        }
    }
}

public class ProcessInstanceItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public int Pid { get; set; }
    public string FormattedPid => Pid.ToString();
    public string Name { get; set; } = "";
    public long CreateTime { get; set; }
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
    public float DiskReadMBps { get; set; }
    public float DiskWriteMBps { get; set; }
    public string FormattedDiskIo => (DiskReadMBps > 0.05f || DiskWriteMBps > 0.05f) ? $"R: {DiskReadMBps:F1} • W: {DiskWriteMBps:F1} MB/s" : "0.0 MB/s";
    public float NetDownSpeedKBps { get; set; }
    public float NetUpSpeedKBps { get; set; }
    public string FormattedNetThroughput => (NetDownSpeedKBps > 0.05f || NetUpSpeedKBps > 0.05f) ? $"↓ {NetworkTracker.FormatSpeed(NetDownSpeedKBps)} • ↑ {NetworkTracker.FormatSpeed(NetUpSpeedKBps)}" : "0 B/s";
    public int EstablishedSockets { get; set; }
    public int ActiveSockets { get; set; }
    public int ThreadCount { get; set; }
    public string Status { get; set; } = "Running";

    public void UpdateFrom(ProcessInstanceItem src)
    {
        if (Pid != src.Pid)
        {
            Pid = src.Pid;
            OnPropertyChanged(nameof(Pid));
            OnPropertyChanged(nameof(FormattedPid));
        }
        if (!string.Equals(Name, src.Name, StringComparison.Ordinal))
        {
            Name = src.Name;
            OnPropertyChanged(nameof(Name));
        }
        CreateTime = src.CreateTime;

        if (Math.Abs(CpuPercent - src.CpuPercent) >= 0.05f)
        {
            CpuPercent = src.CpuPercent;
            OnPropertyChanged(nameof(CpuPercent));
            OnPropertyChanged(nameof(FormattedCpu));
        }
        else
        {
            CpuPercent = src.CpuPercent;
        }

        if (Math.Abs(GpuPercent - src.GpuPercent) >= 0.05f)
        {
            GpuPercent = src.GpuPercent;
            OnPropertyChanged(nameof(GpuPercent));
            OnPropertyChanged(nameof(FormattedGpu));
        }
        else
        {
            GpuPercent = src.GpuPercent;
        }

        GpuVramMb = src.GpuVramMb;

        if (PrivateMemoryBytes != src.PrivateMemoryBytes)
        {
            PrivateMemoryBytes = src.PrivateMemoryBytes;
            OnPropertyChanged(nameof(PrivateMemoryBytes));
            OnPropertyChanged(nameof(FormattedPrivateMemory));
        }

        if (WorkingSetBytes != src.WorkingSetBytes)
        {
            WorkingSetBytes = src.WorkingSetBytes;
            OnPropertyChanged(nameof(WorkingSetBytes));
            OnPropertyChanged(nameof(FormattedWorkingSet));
        }

        DiskReadMBps = src.DiskReadMBps;
        DiskWriteMBps = src.DiskWriteMBps;
        NetDownSpeedKBps = src.NetDownSpeedKBps;
        NetUpSpeedKBps = src.NetUpSpeedKBps;
        EstablishedSockets = src.EstablishedSockets;
        ActiveSockets = src.ActiveSockets;
        ThreadCount = src.ThreadCount;

        if (!string.Equals(Status, src.Status, StringComparison.Ordinal))
        {
            Status = src.Status;
            OnPropertyChanged(nameof(Status));
        }
    }
}

public class ProcessTelemetrySnapshot
{
    public List<ProcessItem> TopCpu { get; set; } = new();
    public List<ProcessItem> TopGpu { get; set; } = new();
    public List<ProcessItem> TopRam { get; set; } = new();
    public List<ProcessItem> TopDiskIo { get; set; } = new();
    public List<ProcessItem> TopNet { get; set; } = new();
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
