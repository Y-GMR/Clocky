using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace Clocky.Core;

public class ProcessTracker : IDisposable
{
    private readonly Dictionary<int, long> _prevCpuTimes = new(512);
    private readonly Dictionary<int, (ulong ReadBytes, ulong WriteBytes)> _prevIoBytes = new(512);
    private readonly Dictionary<int, (long Recv, long Sent)> _prevNetBytes = new(512);
    private DateTime _prevSampleTime = DateTime.UtcNow;
    private readonly int _logicalCoreCount = Math.Max(1, Environment.ProcessorCount);
    private readonly object _syncLock = new();

    // Reusable unmanaged buffer for NtQuerySystemInformation (2MB preallocated)
    private IntPtr _nativeBuffer = IntPtr.Zero;
    private const int NativeBufferSize = 2 * 1024 * 1024;

    // Real-time Kernel ETW Session for exact per-packet byte counters
    private TraceEventSession? _etwSession;
    private Task? _etwTask;
    private readonly ConcurrentDictionary<int, long> _etwRecvBytes = new();
    private readonly ConcurrentDictionary<int, long> _etwSentBytes = new();

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

        StartEtwNetworkTracing();
    }

    private void StartEtwNetworkTracing()
    {
        try
        {
            if (TraceEventSession.IsElevated() == true)
            {
                _etwSession = new TraceEventSession("ClockyKernelNetTrace");
                _etwSession.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

                _etwSession.Source.Kernel.TcpIpRecv += data =>
                {
                    if (data.ProcessID > 0)
                        _etwRecvBytes.AddOrUpdate(data.ProcessID, data.size, (_, prev) => prev + data.size);
                };
                _etwSession.Source.Kernel.TcpIpSend += data =>
                {
                    if (data.ProcessID > 0)
                        _etwSentBytes.AddOrUpdate(data.ProcessID, data.size, (_, prev) => prev + data.size);
                };
                _etwSession.Source.Kernel.UdpIpRecv += data =>
                {
                    if (data.ProcessID > 0)
                        _etwRecvBytes.AddOrUpdate(data.ProcessID, data.size, (_, prev) => prev + data.size);
                };
                _etwSession.Source.Kernel.UdpIpSend += data =>
                {
                    if (data.ProcessID > 0)
                        _etwSentBytes.AddOrUpdate(data.ProcessID, data.size, (_, prev) => prev + data.size);
                };

                _etwTask = Task.Run(() =>
                {
                    try { _etwSession.Source.Process(); } catch { }
                });
            }
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

            try
            {
                _etwSession?.Stop();
                _etwSession?.Dispose();
                _etwSession = null;
            }
            catch { }
        }
    }

    public ProcessTelemetrySnapshot Poll(float systemDownKBps = 0f, float systemUpKBps = 0f)
    {
        lock (_syncLock)
        {
            var now = DateTime.UtcNow;
            double deltaSec = Math.Max(0.2, (now - _prevSampleTime).TotalSeconds);
            _prevSampleTime = now;

            var processMap = new Dictionary<int, ProcessItem>(512);
            var currentCpuTimes = new Dictionary<int, long>(512);
            var currentIoBytes = new Dictionary<int, (ulong ReadBytes, ulong WriteBytes)>(512);
            var currentNetBytes = new Dictionary<int, (long Recv, long Sent)>(512);

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

                            long rawCpuTime = proc.UserTime + proc.KernelTime;
                            currentCpuTimes[pid] = rawCpuTime;
                            currentIoBytes[pid] = ((ulong)proc.ReadTransferCount, (ulong)proc.WriteTransferCount);

                            float cpuUsage = 0f;
                            float diskReadMBps = 0f;
                            float diskWriteMBps = 0f;

                            if (_prevCpuTimes.TryGetValue(pid, out long prevCpu))
                            {
                                long deltaCpu = rawCpuTime - prevCpu;
                                if (deltaCpu > 0)
                                {
                                    double cpuPercentTotal = (deltaCpu / (deltaSec * 10000000.0)) * 100.0;
                                    cpuUsage = (float)Math.Clamp(cpuPercentTotal, 0.0, 100.0 * _logicalCoreCount);
                                }
                            }

                            if (_prevIoBytes.TryGetValue(pid, out var prevIo))
                            {
                                ulong deltaRead = (ulong)proc.ReadTransferCount >= prevIo.ReadBytes ? (ulong)proc.ReadTransferCount - prevIo.ReadBytes : 0;
                                ulong deltaWrite = (ulong)proc.WriteTransferCount >= prevIo.WriteBytes ? (ulong)proc.WriteTransferCount - prevIo.WriteBytes : 0;
                                diskReadMBps = (float)(deltaRead / (deltaSec * 1024.0 * 1024.0));
                                diskWriteMBps = (float)(deltaWrite / (deltaSec * 1024.0 * 1024.0));
                            }

                            float netDownKBps = 0f;
                            float netUpKBps = 0f;
                            if (_etwSession != null)
                            {
                                long curRecv = _etwRecvBytes.GetValueOrDefault(pid, 0L);
                                long curSent = _etwSentBytes.GetValueOrDefault(pid, 0L);
                                currentNetBytes[pid] = (curRecv, curSent);

                                if (_prevNetBytes.TryGetValue(pid, out var prevNet))
                                {
                                    long deltaRecv = curRecv >= prevNet.Recv ? curRecv - prevNet.Recv : 0;
                                    long deltaSent = curSent >= prevNet.Sent ? curSent - prevNet.Sent : 0;
                                    netDownKBps = (float)(deltaRecv / (deltaSec * 1024.0));
                                    netUpKBps = (float)(deltaSent / (deltaSec * 1024.0));
                                }
                            }

                            processMap[pid] = new ProcessItem
                            {
                                Pid = pid,
                                Name = pName,
                                InstanceCount = 1,
                                CpuPercent = cpuUsage,
                                PrivateMemoryBytes = (long)proc.PrivatePageCount,
                                WorkingSetBytes = proc.WorkingSetSize.ToInt64(),
                                DiskReadMBps = diskReadMBps,
                                DiskWriteMBps = diskWriteMBps,
                                NetDownSpeedKBps = netDownKBps,
                                NetUpSpeedKBps = netUpKBps,
                                ThreadCount = (int)proc.NumberOfThreads,
                                Status = "Running"
                            };
                        }

                        if (proc.NextEntryOffset == 0)
                            break;
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

            if (_etwSession != null)
            {
                _prevNetBytes.Clear();
                foreach (var kvp in currentNetBytes)
                {
                    _prevNetBytes[kvp.Key] = kvp.Value;
                }
            }

            if (!DetailedMode)
            {
                return _lastDetailed;
            }

            // 2. Gather GPU Engine Utilization per Process
            PollGpuCounters(processMap);

            // 3. Match active network sockets per PID (refreshed every 2 seconds)
            var (establishedSockets, totalSockets) = GetActiveNetworkSockets();
            foreach (var kvp in totalSockets)
            {
                if (processMap.TryGetValue(kvp.Key, out var pItem))
                {
                    pItem.ActiveSockets = kvp.Value;
                    pItem.EstablishedSockets = establishedSockets.GetValueOrDefault(kvp.Key, 0);
                }
            }

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
                        DiskReadMBps = g.Sum(p => p.DiskReadMBps),
                        DiskWriteMBps = g.Sum(p => p.DiskWriteMBps),
                        EstablishedSockets = g.Sum(p => p.EstablishedSockets),
                        ActiveSockets = g.Sum(p => p.ActiveSockets),
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

            // Top Disk I/O: Apps driving real SSD/HDD read & write bandwidth
            var topDiskIo = leaderboardCandidates
                .Where(p => p.DiskReadMBps > 0.05f || p.DiskWriteMBps > 0.05f)
                .OrderByDescending(p => p.DiskReadMBps + p.DiskWriteMBps)
                .Take(3)
                .ToList();

            if (topDiskIo.Count < 3)
            {
                var fallbackDisk = leaderboardCandidates
                    .Where(p => !topDiskIo.Contains(p))
                    .OrderByDescending(p => p.WorkingSetBytes)
                    .Take(3 - topDiskIo.Count);
                topDiskIo.AddRange(fallbackDisk);
            }

            // Top Active Network: Processes ranked by real-time upload & download throughput, then established connections
            var topNet = leaderboardCandidates
                .Where(p => p.NetDownSpeedKBps > 0.05f || p.NetUpSpeedKBps > 0.05f || p.EstablishedSockets > 0 || p.ActiveSockets > 0)
                .OrderByDescending(p => p.NetDownSpeedKBps + p.NetUpSpeedKBps)
                .ThenByDescending(p => p.EstablishedSockets)
                .ThenByDescending(p => p.ActiveSockets)
                .Take(3)
                .ToList();

            if (topNet.Count < 3)
            {
                var fallbackNet = leaderboardCandidates
                    .Where(p => !topNet.Contains(p))
                    .OrderByDescending(p => p.WorkingSetBytes)
                    .Take(3 - topNet.Count);
                topNet.AddRange(fallbackNet);
            }

            _lastDetailed = new ProcessTelemetrySnapshot
            {
                TopCpu = topCpu,
                TopGpu = topGpu,
                TopRam = topRam,
                TopDiskIo = topDiskIo,
                TopNet = topNet,
                AllProcesses = allList
                    .OrderByDescending(p => p.WorkingSetBytes)
                    .ThenByDescending(p => p.CpuPercent)
                    .ToList()
            };
            return _lastDetailed;
        }
    }

    private static (Dictionary<int, int> Established, Dictionary<int, int> Total) GetActiveNetworkSockets()
    {
        var established = new Dictionary<int, int>();
        var total = new Dictionary<int, int>();
        try
        {
            // 1. IPv4 TCP Connections
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, true, 2, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL);
            if (size > 0)
            {
                IntPtr pTable = Marshal.AllocHGlobal(size);
                try
                {
                    if (GetExtendedTcpTable(pTable, ref size, true, 2, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL) == 0)
                    {
                        int count = Marshal.ReadInt32(pTable);
                        IntPtr rowPtr = IntPtr.Add(pTable, 4);
                        int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                        for (int i = 0; i < count; i++)
                        {
                            var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                            int pid = (int)row.owningPid;
                            if (pid > 4)
                            {
                                total[pid] = total.GetValueOrDefault(pid, 0) + 1;
                                if (row.state == 5) // MIB_TCP_STATE_ESTAB
                                {
                                    established[pid] = established.GetValueOrDefault(pid, 0) + 1;
                                }
                            }
                            rowPtr = IntPtr.Add(rowPtr, rowSize);
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pTable);
                }
            }

            // 2. IPv6 TCP Connections
            size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, true, 23, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL);
            if (size > 0)
            {
                IntPtr pTable = Marshal.AllocHGlobal(size);
                try
                {
                    if (GetExtendedTcpTable(pTable, ref size, true, 23, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL) == 0)
                    {
                        int count = Marshal.ReadInt32(pTable);
                        IntPtr rowPtr = IntPtr.Add(pTable, 4);
                        int rowSize = Marshal.SizeOf<MIB_TCP6ROW_OWNER_PID>();
                        for (int i = 0; i < count; i++)
                        {
                            var row = Marshal.PtrToStructure<MIB_TCP6ROW_OWNER_PID>(rowPtr);
                            int pid = (int)row.owningPid;
                            if (pid > 4)
                            {
                                total[pid] = total.GetValueOrDefault(pid, 0) + 1;
                                if (row.state == 5) // MIB_TCP_STATE_ESTAB
                                {
                                    established[pid] = established.GetValueOrDefault(pid, 0) + 1;
                                }
                            }
                            rowPtr = IntPtr.Add(rowPtr, rowSize);
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pTable);
                }
            }

            // 3. UDP Listeners
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
                            int pid = (int)row.owningPid;
                            if (pid > 4)
                            {
                                total[pid] = total.GetValueOrDefault(pid, 0) + 1;
                            }
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
        return (established, total);
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
    private struct MIB_TCP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] localAddr;
        public uint localScopeId;
        public uint localPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] remoteAddr;
        public uint remoteScopeId;
        public uint remotePort;
        public uint state;
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
