using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace Clocky.Core;

public class ProcessTracker : IDisposable
{
    private readonly Dictionary<int, long> _prevCpuTimes = new(512);
    private readonly Dictionary<int, (ulong ReadBytes, ulong WriteBytes)> _prevIoBytes = new(512);
    private DateTime _prevSampleTime = DateTime.UtcNow;
    private readonly int _logicalCoreCount = Math.Max(1, Environment.ProcessorCount);
    private readonly object _syncLock = new();

    // Reusable unmanaged buffer for NtQuerySystemInformation (2MB preallocated)
    private IntPtr _nativeBuffer = IntPtr.Zero;
    private const int NativeBufferSize = 2 * 1024 * 1024;

    // Cache of active network PIDs (refreshed every 2 seconds)
    private HashSet<int> _cachedNetPids = new();
    private DateTime _lastNetPidRefresh = DateTime.MinValue;

    // Cache of GPU Performance Counters: InstanceName -> Counter (refreshed every 15s)
    private readonly Dictionary<string, PerformanceCounter> _gpuCounters = new();
    private DateTime _lastGpuCounterRefresh = DateTime.MinValue;

    public bool DetailedMode { get; set; } = false;
    private ProcessTelemetrySnapshot _lastDetailed = new();

    public ProcessTracker()
    {
        try
        {
            _nativeBuffer = Marshal.AllocHGlobal(NativeBufferSize);
        }
        catch { }
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_nativeBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_nativeBuffer);
                _nativeBuffer = IntPtr.Zero;
            }
            foreach (var ctr in _gpuCounters.Values)
            {
                try { ctr.Dispose(); } catch { }
            }
            _gpuCounters.Clear();
        }
    }

    public ProcessTelemetrySnapshot Poll()
    {
        lock (_syncLock)
        {
            var now = DateTime.UtcNow;
            double deltaSec = Math.Max(0.2, (now - _prevSampleTime).TotalSeconds);
            _prevSampleTime = now;

            var processMap = new Dictionary<int, ProcessItem>(512);
            var currentCpuTimes = new Dictionary<int, long>(512);
            var currentIoBytes = new Dictionary<int, (ulong Read, ulong Write)>(512);

            // 1. Fast Native Kernel Process Enumeration via NtQuerySystemInformation
            if (_nativeBuffer != IntPtr.Zero)
            {
                int returnLength = 0;
                int status = NtQuerySystemInformation(SystemProcessInformation, _nativeBuffer, NativeBufferSize, out returnLength);
                if (status == 0) // STATUS_SUCCESS
                {
                    IntPtr curr = _nativeBuffer;
                    while (true)
                    {
                        var proc = Marshal.PtrToStructure<SYSTEM_PROCESS_INFORMATION>(curr);
                        int pid = proc.UniqueProcessId.ToInt32();

                        if (pid > 0) // Skip System Idle Process (PID 0)
                        {
                            string pName = proc.ImageName.Buffer != IntPtr.Zero && proc.ImageName.Length > 0
                                ? Marshal.PtrToStringUni(proc.ImageName.Buffer, proc.ImageName.Length / 2)
                                : "System";

                            // Strip .exe extension if present for clean UI display
                            if (pName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                pName = pName.Substring(0, pName.Length - 4);

                            long curCpuTime = proc.UserTime + proc.KernelTime;
                            currentCpuTimes[pid] = curCpuTime;

                            float cpuPct = 0f;
                            if (_prevCpuTimes.TryGetValue(pid, out long prevCpu))
                            {
                                long deltaTicks = curCpuTime - prevCpu;
                                if (deltaTicks > 0)
                                {
                                    // 100-nanosecond units to milliseconds: divide by 10,000
                                    double deltaMs = deltaTicks / 10000.0;
                                    cpuPct = (float)Math.Clamp((deltaMs / (deltaSec * 1000.0 * _logicalCoreCount)) * 100.0, 0.0, 100.0);
                                }
                            }

                            ulong readBytes = (ulong)Math.Max(0, proc.ReadTransferCount);
                            ulong writeBytes = (ulong)Math.Max(0, proc.WriteTransferCount);
                            currentIoBytes[pid] = (readBytes, writeBytes);

                            float downKBps = 0f;
                            float upKBps = 0f;
                            if (_prevIoBytes.TryGetValue(pid, out var prevIo) && (prevIo.ReadBytes > 0 || prevIo.WriteBytes > 0))
                            {
                                ulong deltaRead = readBytes >= prevIo.ReadBytes ? readBytes - prevIo.ReadBytes : 0;
                                ulong deltaWrite = writeBytes >= prevIo.WriteBytes ? writeBytes - prevIo.WriteBytes : 0;

                                downKBps = (float)(deltaRead / (deltaSec * 1024.0));
                                upKBps = (float)(deltaWrite / (deltaSec * 1024.0));

                                // Sanity check clamp
                                if (downKBps > 500f * 1024f) downKBps = 0f;
                                if (upKBps > 500f * 1024f) upKBps = 0f;
                            }

                            long wsMem = proc.WorkingSetSize.ToInt64();
                            long privMem = proc.PagefileUsage.ToInt64();
                            if (privMem == 0 && proc.PrivatePageCount != IntPtr.Zero)
                                privMem = proc.PrivatePageCount.ToInt64() * 4096;

                            var item = new ProcessItem
                            {
                                Pid = pid,
                                Name = pName,
                                CpuPercent = cpuPct,
                                PrivateMemoryBytes = privMem,
                                WorkingSetBytes = wsMem,
                                NetDownSpeedKBps = downKBps,
                                NetUpSpeedKBps = upKBps,
                                ThreadCount = (int)proc.NumberOfThreads,
                                Status = "Running"
                            };

                            processMap[pid] = item;
                        }

                        if (proc.NextEntryOffset == 0) break;
                        curr = IntPtr.Add(curr, (int)proc.NextEntryOffset);
                    }
                }
            }

            _prevCpuTimes.Clear();
            foreach (var kvp in currentCpuTimes)
            {
                _prevCpuTimes[kvp.Key] = kvp.Value;
            }

            _prevIoBytes.Clear();
            foreach (var kvp in currentIoBytes)
            {
                _prevIoBytes[kvp.Key] = kvp.Value;
            }

            if (!DetailedMode)
            {
                return _lastDetailed;
            }

            // 2. Gather GPU Engine Utilization per Process
            PollGpuCounters(processMap);

            // 3. Match active network sockets to refine network priority (cached for 2 seconds)
            if ((now - _lastNetPidRefresh).TotalSeconds >= 2.0 || _cachedNetPids.Count == 0)
            {
                _lastNetPidRefresh = now;
                _cachedNetPids = GetActiveNetworkPids();
            }
            var netPids = _cachedNetPids;

            // Group and bundle processes with the exact same name (e.g. multi-process browsers, IDE workers)
            var bundledList = processMap.Values
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var primary = g.OrderByDescending(p => p.WorkingSetBytes).First();
                    int count = g.Count();
                    return new ProcessItem
                    {
                        Pid = primary.Pid,
                        Name = primary.Name,
                        InstanceCount = count,
                        CpuPercent = (float)Math.Clamp(g.Sum(p => (double)p.CpuPercent), 0.0, 100.0 * _logicalCoreCount),
                        GpuPercent = g.Sum(p => p.GpuPercent),
                        GpuVramMb = g.Sum(p => p.GpuVramMb),
                        PrivateMemoryBytes = g.Sum(p => p.PrivateMemoryBytes),
                        WorkingSetBytes = g.Sum(p => p.WorkingSetBytes),
                        NetDownSpeedKBps = g.Sum(p => p.NetDownSpeedKBps),
                        NetUpSpeedKBps = g.Sum(p => p.NetUpSpeedKBps),
                        ThreadCount = g.Sum(p => p.ThreadCount),
                        Status = "Running"
                    };
                })
                .ToList();

            var allList = bundledList;

            // 4. Derive Top 3 Lists (Strictly exclude Clocky itself from all leaderboards)
            int currentPid = Environment.ProcessId;
            var leaderboardCandidates = allList
                .Where(p => p.Pid != currentPid && !p.Name.Equals("Clocky", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var topCpu = leaderboardCandidates
                .Where(p => p.CpuPercent > 0.05f)
                .OrderByDescending(p => p.CpuPercent)
                .Take(3)
                .ToList();

            if (topCpu.Count < 3)
            {
                topCpu = leaderboardCandidates.OrderByDescending(p => p.CpuPercent).Take(3).ToList();
            }

            var topRam = leaderboardCandidates
                .Where(p => !p.Name.Equals("Memory Compression", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.WorkingSetBytes)
                .Take(3)
                .ToList();

            var topGpu = leaderboardCandidates
                .Where(p => p.GpuPercent > 0.05f || p.GpuVramMb > 0f)
                .OrderByDescending(p => p.GpuPercent)
                .ThenByDescending(p => p.GpuVramMb)
                .Take(3)
                .ToList();

            if (topGpu.Count < 3)
            {
                topGpu = leaderboardCandidates.OrderByDescending(p => p.GpuPercent).Take(3).ToList();
            }

            // Top Network: Prioritize network-connected processes with active I/O
            var topNetDown = leaderboardCandidates
                .Where(p => p.NetDownSpeedKBps > 0.05f && (netPids.Contains(p.Pid) || IsCommonNetworkApp(p.Name)))
                .OrderByDescending(p => p.NetDownSpeedKBps)
                .Take(3)
                .ToList();

            if (topNetDown.Count < 3)
            {
                var fallbackDown = leaderboardCandidates
                    .Where(p => (netPids.Contains(p.Pid) || IsCommonNetworkApp(p.Name)) && !topNetDown.Contains(p))
                    .OrderByDescending(p => p.NetDownSpeedKBps)
                    .ThenByDescending(p => p.WorkingSetBytes)
                    .Take(3 - topNetDown.Count);
                topNetDown.AddRange(fallbackDown);
            }

            var topNetUp = leaderboardCandidates
                .Where(p => p.NetUpSpeedKBps > 0.05f && (netPids.Contains(p.Pid) || IsCommonNetworkApp(p.Name)))
                .OrderByDescending(p => p.NetUpSpeedKBps)
                .Take(3)
                .ToList();

            if (topNetUp.Count < 3)
            {
                var fallbackUp = leaderboardCandidates
                    .Where(p => (netPids.Contains(p.Pid) || IsCommonNetworkApp(p.Name)) && !topNetUp.Contains(p))
                    .OrderByDescending(p => p.NetUpSpeedKBps)
                    .ThenByDescending(p => p.WorkingSetBytes)
                    .Take(3 - topNetUp.Count);
                topNetUp.AddRange(fallbackUp);
            }

            _lastDetailed = new ProcessTelemetrySnapshot
            {
                TopCpu = topCpu,
                TopGpu = topGpu,
                TopRam = topRam,
                TopNetDown = topNetDown,
                TopNetUp = topNetUp,
                AllProcesses = allList
                    .OrderByDescending(p => p.WorkingSetBytes)
                    .ThenByDescending(p => p.CpuPercent)
                    .ToList()
            };
            return _lastDetailed;
        }
    }

    private static bool IsCommonNetworkApp(string pName)
    {
        string name = pName.ToLowerInvariant();
        return name.Contains("chrome") ||
               name.Contains("librewolf") ||
               name.Contains("firefox") ||
               name.Contains("msedge") ||
               name.Contains("brave") ||
               name.Contains("discord") ||
               name.Contains("steam") ||
               name.Contains("spotify") ||
               name.Contains("antigravity") ||
               name.Contains("code") ||
               name.Contains("language_server") ||
               name.Contains("python") ||
               name.Contains("node") ||
               name.Contains("git") ||
               name.Contains("curl") ||
               name.Contains("msedgewebview2") ||
               name.Contains("telegram") ||
               name.Contains("slack") ||
               name.Contains("qbittorrent") ||
               name.Contains("epicgames");
    }

    private static HashSet<int> GetActiveNetworkPids()
    {
        var pids = new HashSet<int>();
        try
        {
            // 1. TCP Connections
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, true, 2, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_CONNECTIONS);
            if (size > 0)
            {
                IntPtr pTable = Marshal.AllocHGlobal(size);
                try
                {
                    if (GetExtendedTcpTable(pTable, ref size, true, 2, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_CONNECTIONS) == 0)
                    {
                        int count = Marshal.ReadInt32(pTable);
                        IntPtr rowPtr = IntPtr.Add(pTable, 4);
                        int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                        for (int i = 0; i < count; i++)
                        {
                            var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                            if (row.owningPid > 4) pids.Add((int)row.owningPid);
                            rowPtr = IntPtr.Add(rowPtr, rowSize);
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pTable);
                }
            }

            // 2. UDP Listeners
            size = 0;
            GetExtendedUdpTable(IntPtr.Zero, ref size, true, 2, UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID);
            if (size > 0)
            {
                IntPtr pTable = Marshal.AllocHGlobal(size);
                try
                {
                    if (GetExtendedUdpTable(pTable, ref size, true, 2, UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID) == 0)
                    {
                        int count = Marshal.ReadInt32(pTable);
                        IntPtr rowPtr = IntPtr.Add(pTable, 4);
                        int rowSize = Marshal.SizeOf<MIB_UDPROW_OWNER_PID>();
                        for (int i = 0; i < count; i++)
                        {
                            var row = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(rowPtr);
                            if (row.owningPid > 4) pids.Add((int)row.owningPid);
                            rowPtr = IntPtr.Add(rowPtr, rowSize);
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pTable);
                }
            }
        }
        catch { }
        return pids;
    }

    private void PollGpuCounters(Dictionary<int, ProcessItem> processMap)
    {
        try
        {
            if ((DateTime.UtcNow - _lastGpuCounterRefresh).TotalSeconds > 15)
            {
                _lastGpuCounterRefresh = DateTime.UtcNow;
                try
                {
                    var cat = new PerformanceCounterCategory("GPU Engine");
                    var insts = cat.GetInstanceNames();
                    var activeInsts = new HashSet<string>(insts.Where(i => i.Contains("pid_") && (i.Contains("engtype_3D") || i.Contains("engtype_Compute"))));

                    var toRemove = _gpuCounters.Keys.Where(k => !activeInsts.Contains(k)).ToList();
                    foreach (var k in toRemove)
                    {
                        _gpuCounters[k].Dispose();
                        _gpuCounters.Remove(k);
                    }

                    foreach (var inst in activeInsts)
                    {
                        if (!_gpuCounters.ContainsKey(inst))
                        {
                            try
                            {
                                var ctr = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, true);
                                ctr.NextValue();
                                _gpuCounters[inst] = ctr;
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            foreach (var (inst, ctr) in _gpuCounters)
            {
                try
                {
                    float val = ctr.NextValue();
                    if (val > 0.01f)
                    {
                        int pidStart = inst.IndexOf("pid_");
                        if (pidStart >= 0)
                        {
                            int pidEnd = inst.IndexOf('_', pidStart + 4);
                            if (pidEnd > pidStart)
                            {
                                if (int.TryParse(inst.Substring(pidStart + 4, pidEnd - pidStart - 4), out int pid))
                                {
                                    if (processMap.TryGetValue(pid, out var item))
                                    {
                                        item.GpuPercent += val;
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    #region Win32 P/Invoke Definitions

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int SystemInformationClass,
        IntPtr SystemInformation,
        int SystemInformationLength,
        out int ReturnLength);

    private const int SystemProcessInformation = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESS_INFORMATION
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public UNICODE_STRING ImageName;
        public int BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
        public uint HandleCount;
        public uint SessionId;
        public UIntPtr UniqueProcessKey;
        public IntPtr PeakVirtualSize;
        public IntPtr VirtualSize;
        public uint PageFaultCount;
        public IntPtr PeakWorkingSetSize;
        public IntPtr WorkingSetSize;
        public IntPtr QuotaPeakPagedPoolUsage;
        public IntPtr QuotaPagedPoolUsage;
        public IntPtr QuotaPeakNonPagedPoolUsage;
        public IntPtr QuotaNonPagedPoolUsage;
        public IntPtr PagefileUsage;
        public IntPtr PeakPagefileUsage;
        public IntPtr PrivatePageCount;
        public long ReadOperationCount;
        public long WriteOperationCount;
        public long OtherOperationCount;
        public long ReadTransferCount;
        public long WriteTransferCount;
        public long OtherTransferCount;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        bool bOrder,
        int ulAf,
        TCP_TABLE_CLASS TableClass,
        uint reserved = 0);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable,
        ref int pdwSize,
        bool bOrder,
        int ulAf,
        UDP_TABLE_CLASS TableClass,
        uint reserved = 0);

    private enum TCP_TABLE_CLASS
    {
        TCP_TABLE_BASIC_LISTENER,
        TCP_TABLE_BASIC_CONNECTIONS,
        TCP_TABLE_BASIC_ALL,
        TCP_TABLE_OWNER_PID_LISTENER,
        TCP_TABLE_OWNER_PID_CONNECTIONS,
        TCP_TABLE_OWNER_PID_ALL,
        TCP_TABLE_OWNER_MODULE_LISTENER,
        TCP_TABLE_OWNER_MODULE_CONNECTIONS,
        TCP_TABLE_OWNER_MODULE_ALL
    }

    private enum UDP_TABLE_CLASS
    {
        UDP_TABLE_BASIC,
        UDP_TABLE_OWNER_PID,
        UDP_TABLE_OWNER_MODULE
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public uint localPort;
        public uint remoteAddr;
        public uint remotePort;
        public uint owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID
    {
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint owningPid;
    }

    #endregion
}
