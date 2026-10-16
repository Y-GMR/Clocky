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
    private Dictionary<(int Pid, long CreateTime), long> _prevCpuTimes = new(512);
    private Dictionary<(int Pid, long CreateTime), long> _currCpuTimes = new(512);
    private Dictionary<(int Pid, long CreateTime), (ulong ReadBytes, ulong WriteBytes)> _prevIoBytes = new(512);
    private Dictionary<(int Pid, long CreateTime), (ulong ReadBytes, ulong WriteBytes)> _currIoBytes = new(512);
    private Dictionary<(int Pid, long CreateTime), (long Recv, long Sent)> _prevNetBytes = new(512);
    private Dictionary<(int Pid, long CreateTime), (long Recv, long Sent)> _currNetBytes = new(512);
    private DateTime _prevSampleTime = DateTime.UtcNow;
    private double _lastValidDeltaSec = 1.0;
    private readonly int _logicalCoreCount = Math.Max(1, Environment.ProcessorCount);
    private readonly object _syncLock = new();

    // Reusable unmanaged buffer for NtQuerySystemInformation (dynamically grows if needed)
    private IntPtr _nativeBuffer = IntPtr.Zero;
    private int _nativeBufferSize = 2 * 1024 * 1024;

    // Real-time Kernel ETW Session for exact per-packet byte counters
    private TraceEventSession? _etwSession;
    private Task? _etwTask;
    private readonly ConcurrentDictionary<int, long> _etwRecvBytes = new();
    private readonly ConcurrentDictionary<int, long> _etwSentBytes = new();

    // Cache of active network sockets per PID (refreshed every 2 seconds)
    private Dictionary<int, int> _cachedEstablishedSockets = new();
    private Dictionary<int, int> _cachedTotalSockets = new();
    private DateTime _lastNetPidRefresh = DateTime.MinValue;

    // Batch GPU PerformanceCounterCategory readers and CounterSample state (zero per-instance handles)
    private PerformanceCounterCategory? _gpuEngineCategory;
    private PerformanceCounterCategory? _gpuMemoryCategory;
    private Dictionary<string, (CounterSample Sample, int Pid)> _prevGpuSamples = new(512);
    private Dictionary<string, (CounterSample Sample, int Pid)> _currGpuSamples = new(512);
    private static readonly List<ProcessInstanceItem> s_emptyChildren = new(0);
    private DateTime _lastGpuCategoryCheck = DateTime.MinValue;
    private bool _gpuCategoriesChecked;

    public bool DetailedMode { get; set; } = false;
    public GpuProcessEngineState GpuEngineState { get; private set; } = GpuProcessEngineState.Active;
    private ProcessTelemetrySnapshot _lastDetailed = new();

    public ProcessTracker()
    {
        try
        {
            _nativeBuffer = Marshal.AllocHGlobal(_nativeBufferSize);
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
                const string sessionName = "ClockyKernelNetTrace";
                try
                {
                    var existingSession = TraceEventSession.GetActiveSession(sessionName);
                    if (existingSession != null)
                    {
                        existingSession.Stop(true);
                        existingSession.Dispose();
                    }
                }
                catch { }

                _etwSession = new TraceEventSession(sessionName);
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
            _prevGpuSamples.Clear();
            _currGpuSamples.Clear();

            try
            {
                _etwSession?.Stop();
                _etwTask?.Wait(500);
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
            double rawElapsed = (now - _prevSampleTime).TotalSeconds;
            bool advanceBaseline = rawElapsed >= 0.25;
            double deltaSec = advanceBaseline ? rawElapsed : Math.Max(0.2, _lastValidDeltaSec + rawElapsed);

            bool detailed = DetailedMode;
            var processMap = detailed ? new Dictionary<int, ProcessItem>(512) : null;
            bool usePriorTickBaseline = !advanceBaseline && _currCpuTimes.Count > 0;
            var baseCpu = usePriorTickBaseline ? _currCpuTimes : _prevCpuTimes;
            var baseIo = usePriorTickBaseline ? _currIoBytes : _prevIoBytes;
            var baseNet = usePriorTickBaseline ? _currNetBytes : _prevNetBytes;

            if (advanceBaseline)
            {
                _currCpuTimes.Clear();
                _currIoBytes.Clear();
                if (_etwSession != null) _currNetBytes.Clear();
            }

            // 1. Fast Native Kernel Process Enumeration via NtQuerySystemInformation
            if (_nativeBuffer != IntPtr.Zero)
            {
                int returnLength = 0;
                int status = NtQuerySystemInformation(SystemProcessInformation, _nativeBuffer, _nativeBufferSize, out returnLength);
                if (status == unchecked((int)0xC0000004) && returnLength > _nativeBufferSize) // STATUS_INFO_LENGTH_MISMATCH
                {
                    try
                    {
                        int newSize = Math.Max(_nativeBufferSize * 2, returnLength + 65536);
                        IntPtr newBuf = Marshal.AllocHGlobal(newSize);
                        Marshal.FreeHGlobal(_nativeBuffer);
                        _nativeBuffer = newBuf;
                        _nativeBufferSize = newSize;
                        status = NtQuerySystemInformation(SystemProcessInformation, _nativeBuffer, _nativeBufferSize, out returnLength);
                    }
                    catch { }
                }

                if (status == 0) // STATUS_SUCCESS
                {
                    IntPtr curr = _nativeBuffer;
                    while (true)
                    {
                        var proc = Marshal.PtrToStructure<SYSTEM_PROCESS_INFORMATION>(curr);
                        int pid = proc.UniqueProcessId.ToInt32();

                        if (pid > 0) // Skip System Idle Process (PID 0)
                        {
                            long createTime = proc.CreateTime;
                            var procKey = (pid, createTime);
                            long rawCpuTime = proc.UserTime + proc.KernelTime;
                            ulong readBytes = (ulong)proc.ReadTransferCount;
                            ulong writeBytes = (ulong)proc.WriteTransferCount;

                            long curRecv = 0L;
                            long curSent = 0L;
                            if (_etwSession != null)
                            {
                                curRecv = _etwRecvBytes.GetValueOrDefault(pid, 0L);
                                curSent = _etwSentBytes.GetValueOrDefault(pid, 0L);
                            }

                            if (advanceBaseline)
                            {
                                _currCpuTimes[procKey] = rawCpuTime;
                                _currIoBytes[procKey] = (readBytes, writeBytes);
                                if (_etwSession != null)
                                    _currNetBytes[procKey] = (curRecv, curSent);
                            }

                            if (detailed && processMap != null)
                            {
                                string pName = proc.ImageName.Buffer != IntPtr.Zero && proc.ImageName.Length > 0
                                    ? Marshal.PtrToStringUni(proc.ImageName.Buffer, proc.ImageName.Length / 2)
                                    : "System";

                                if (pName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                    pName = pName.Substring(0, pName.Length - 4);

                                float cpuUsage = 0f;
                                float diskReadMBps = 0f;
                                float diskWriteMBps = 0f;

                                if (baseCpu.TryGetValue(procKey, out long prevCpu))
                                {
                                    long deltaCpu = rawCpuTime - prevCpu;
                                    if (deltaCpu > 0)
                                    {
                                        double cpuPercentTotal = (deltaCpu / (deltaSec * 10000000.0 * _logicalCoreCount)) * 100.0;
                                        cpuUsage = (float)Math.Clamp(cpuPercentTotal, 0.0, 100.0);
                                    }
                                }

                                if (baseIo.TryGetValue(procKey, out var prevIo))
                                {
                                    ulong deltaRead = readBytes >= prevIo.ReadBytes ? readBytes - prevIo.ReadBytes : 0;
                                    ulong deltaWrite = writeBytes >= prevIo.WriteBytes ? writeBytes - prevIo.WriteBytes : 0;
                                    diskReadMBps = (float)(deltaRead / (deltaSec * 1024.0 * 1024.0));
                                    diskWriteMBps = (float)(deltaWrite / (deltaSec * 1024.0 * 1024.0));
                                }

                                float netDownKBps = 0f;
                                float netUpKBps = 0f;
                                if (_etwSession != null && baseNet.TryGetValue(procKey, out var prevNet))
                                {
                                    long deltaRecv = curRecv >= prevNet.Recv ? curRecv - prevNet.Recv : 0;
                                    long deltaSent = curSent >= prevNet.Sent ? curSent - prevNet.Sent : 0;
                                    netDownKBps = (float)(deltaRecv / (deltaSec * 1024.0));
                                    netUpKBps = (float)(deltaSent / (deltaSec * 1024.0));
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
                        }

                        if (proc.NextEntryOffset == 0)
                            break;
                        curr = IntPtr.Add(curr, (int)proc.NextEntryOffset);
                    }
                }
            }

            if (advanceBaseline && !detailed)
            {
                (_prevCpuTimes, _currCpuTimes) = (_currCpuTimes, _prevCpuTimes);
                (_prevIoBytes, _currIoBytes) = (_currIoBytes, _prevIoBytes);
                if (_etwSession != null)
                    (_prevNetBytes, _currNetBytes) = (_currNetBytes, _prevNetBytes);
                _prevSampleTime = now;
                _lastValidDeltaSec = deltaSec;
                return _lastDetailed;
            }

            if (!detailed || processMap == null)
            {
                return _lastDetailed;
            }

            if (advanceBaseline)
            {
                (_prevCpuTimes, _currCpuTimes) = (_currCpuTimes, _prevCpuTimes);
                (_prevIoBytes, _currIoBytes) = (_currIoBytes, _prevIoBytes);
                if (_etwSession != null)
                    (_prevNetBytes, _currNetBytes) = (_currNetBytes, _prevNetBytes);
                _prevSampleTime = now;
                _lastValidDeltaSec = deltaSec;
            }

            // 2. Gather GPU Engine Utilization and Dedicated/Local VRAM per Process via single-call ReadCategory()
            PollGpuCounters(processMap);

            // 3. Match active network sockets per PID (cached for 2 seconds)
            var (establishedSockets, totalSockets) = GetActiveNetworkSockets(now);
            foreach (var kvp in totalSockets)
            {
                if (processMap.TryGetValue(kvp.Key, out var pItem))
                {
                    pItem.ActiveSockets = kvp.Value;
                    pItem.EstablishedSockets = establishedSockets.GetValueOrDefault(kvp.Key, 0);
                }
            }

            // Group and bundle processes with the exact same name (fast-path for single-instance processes)
            var groups = new Dictionary<string, List<ProcessItem>>(processMap.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var p in processMap.Values)
            {
                if (!groups.TryGetValue(p.Name, out var list))
                {
                    list = new List<ProcessItem>(1);
                    groups[p.Name] = list;
                }
                list.Add(p);
            }

            var allList = new List<ProcessItem>(groups.Count);
            foreach (var g in groups.Values)
            {
                int count = g.Count;
                if (count == 1)
                {
                    var single = g[0];
                    single.CpuPercent = Math.Clamp(single.CpuPercent, 0f, 100f);
                    single.GpuPercent = Math.Clamp(single.GpuPercent, 0f, 100f);
                    single.Children = s_emptyChildren;
                    allList.Add(single);
                    continue;
                }

                g.Sort((a, b) => b.WorkingSetBytes.CompareTo(a.WorkingSetBytes));
                var primary = g[0];
                var children = new List<ProcessInstanceItem>(count);
                double sumCpu = 0;
                float sumGpu = 0f, sumVram = 0f, sumRead = 0f, sumWrite = 0f, sumDown = 0f, sumUp = 0f;
                long sumPriv = 0, sumWork = 0;
                int sumEst = 0, sumAct = 0, sumThreads = 0;

                for (int i = 0; i < count; i++)
                {
                    var p = g[i];
                    sumCpu += p.CpuPercent;
                    sumGpu += p.GpuPercent;
                    sumVram += p.GpuVramMb;
                    sumPriv += p.PrivateMemoryBytes;
                    sumWork += p.WorkingSetBytes;
                    sumRead += p.DiskReadMBps;
                    sumWrite += p.DiskWriteMBps;
                    sumEst += p.EstablishedSockets;
                    sumAct += p.ActiveSockets;
                    sumDown += p.NetDownSpeedKBps;
                    sumUp += p.NetUpSpeedKBps;
                    sumThreads += p.ThreadCount;

                    children.Add(new ProcessInstanceItem
                    {
                        Pid = p.Pid,
                        Name = p.Name,
                        CpuPercent = p.CpuPercent,
                        GpuPercent = Math.Clamp(p.GpuPercent, 0f, 100f),
                        GpuVramMb = p.GpuVramMb,
                        PrivateMemoryBytes = p.PrivateMemoryBytes,
                        WorkingSetBytes = p.WorkingSetBytes,
                        DiskReadMBps = p.DiskReadMBps,
                        DiskWriteMBps = p.DiskWriteMBps,
                        EstablishedSockets = p.EstablishedSockets,
                        ActiveSockets = p.ActiveSockets,
                        NetDownSpeedKBps = p.NetDownSpeedKBps,
                        NetUpSpeedKBps = p.NetUpSpeedKBps,
                        ThreadCount = p.ThreadCount,
                        Status = p.Status
                    });
                }

                allList.Add(new ProcessItem
                {
                    Pid = primary.Pid,
                    Name = primary.Name,
                    InstanceCount = count,
                    CpuPercent = (float)Math.Clamp(sumCpu, 0.0, 100.0),
                    GpuPercent = Math.Clamp(sumGpu, 0f, 100f),
                    GpuVramMb = sumVram,
                    PrivateMemoryBytes = sumPriv,
                    WorkingSetBytes = sumWork,
                    DiskReadMBps = sumRead,
                    DiskWriteMBps = sumWrite,
                    EstablishedSockets = sumEst,
                    ActiveSockets = sumAct,
                    NetDownSpeedKBps = sumDown,
                    NetUpSpeedKBps = sumUp,
                    ThreadCount = sumThreads,
                    Status = "Running",
                    Children = children
                });
            }

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

            // Top Disk I/O: Apps driving real SSD/HDD read & write bandwidth
            var topDiskIo = leaderboardCandidates
                .Where(p => p.DiskReadMBps > 0.05f || p.DiskWriteMBps > 0.05f)
                .OrderByDescending(p => p.DiskReadMBps + p.DiskWriteMBps)
                .Take(3)
                .ToList();

            // Top Active Network: Processes ranked by real-time upload & download throughput, then established connections
            var topNet = leaderboardCandidates
                .Where(p => p.NetDownSpeedKBps > 0.05f || p.NetUpSpeedKBps > 0.05f || p.EstablishedSockets > 0 || p.ActiveSockets > 0)
                .OrderByDescending(p => p.NetDownSpeedKBps + p.NetUpSpeedKBps)
                .ThenByDescending(p => p.EstablishedSockets)
                .ThenByDescending(p => p.ActiveSockets)
                .Take(3)
                .ToList();

            allList.Sort((a, b) =>
            {
                int cmp = b.WorkingSetBytes.CompareTo(a.WorkingSetBytes);
                return cmp != 0 ? cmp : b.CpuPercent.CompareTo(a.CpuPercent);
            });

            _lastDetailed = new ProcessTelemetrySnapshot
            {
                TopCpu = topCpu,
                TopGpu = topGpu,
                TopRam = topRam,
                TopDiskIo = topDiskIo,
                TopNet = topNet,
                AllProcesses = allList
            };
            return _lastDetailed;
        }
    }

    private (Dictionary<int, int> Established, Dictionary<int, int> Total) GetActiveNetworkSockets(DateTime now)
    {
        if ((now - _lastNetPidRefresh).TotalSeconds < 2.0 && _cachedTotalSockets.Count > 0)
        {
            return (_cachedEstablishedSockets, _cachedTotalSockets);
        }

        _lastNetPidRefresh = now;
        var established = new Dictionary<int, int>(128);
        var total = new Dictionary<int, int>(128);
        try
        {
            // 1. IPv4 TCP Connections (MIB_TCPROW_OWNER_PID: 24 bytes; state @ 0, owningPid @ 20)
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL);
            if (size > 0)
            {
                IntPtr pTable = Marshal.AllocHGlobal(size);
                try
                {
                    if (GetExtendedTcpTable(pTable, ref size, false, 2, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL) == 0)
                    {
                        int count = Marshal.ReadInt32(pTable);
                        IntPtr rowPtr = IntPtr.Add(pTable, 4);
                        const int rowSize = 24;
                        for (int i = 0; i < count; i++)
                        {
                            int pid = Marshal.ReadInt32(rowPtr, 20);
                            if (pid > 4)
                            {
                                total[pid] = total.GetValueOrDefault(pid, 0) + 1;
                                if (Marshal.ReadInt32(rowPtr, 0) == 5) // MIB_TCP_STATE_ESTAB
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

            // 2. IPv6 TCP Connections (MIB_TCP6ROW_OWNER_PID: 56 bytes; state @ 48, owningPid @ 52)
            size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, 23, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL);
            if (size > 0)
            {
                IntPtr pTable = Marshal.AllocHGlobal(size);
                try
                {
                    if (GetExtendedTcpTable(pTable, ref size, false, 23, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL) == 0)
                    {
                        int count = Marshal.ReadInt32(pTable);
                        IntPtr rowPtr = IntPtr.Add(pTable, 4);
                        const int rowSize = 56;
                        for (int i = 0; i < count; i++)
                        {
                            int pid = Marshal.ReadInt32(rowPtr, 52);
                            if (pid > 4)
                            {
                                total[pid] = total.GetValueOrDefault(pid, 0) + 1;
                                if (Marshal.ReadInt32(rowPtr, 48) == 5) // MIB_TCP_STATE_ESTAB
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

            // 3. IPv4 UDP Listeners (MIB_UDPROW_OWNER_PID: 12 bytes; owningPid @ 8)
            size = 0;
            GetExtendedUdpTable(IntPtr.Zero, ref size, false, 2, UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID);
            if (size > 0)
            {
                IntPtr pTable = Marshal.AllocHGlobal(size);
                try
                {
                    if (GetExtendedUdpTable(pTable, ref size, false, 2, UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID) == 0)
                    {
                        int count = Marshal.ReadInt32(pTable);
                        IntPtr rowPtr = IntPtr.Add(pTable, 4);
                        const int rowSize = 12;
                        for (int i = 0; i < count; i++)
                        {
                            int pid = Marshal.ReadInt32(rowPtr, 8);
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

            // 4. IPv6 UDP Listeners (MIB_UDP6ROW_OWNER_PID: 28 bytes; owningPid @ 24)
            size = 0;
            GetExtendedUdpTable(IntPtr.Zero, ref size, false, 23, UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID);
            if (size > 0)
            {
                IntPtr pTable = Marshal.AllocHGlobal(size);
                try
                {
                    if (GetExtendedUdpTable(pTable, ref size, false, 23, UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID) == 0)
                    {
                        int count = Marshal.ReadInt32(pTable);
                        IntPtr rowPtr = IntPtr.Add(pTable, 4);
                        const int rowSize = 28;
                        for (int i = 0; i < count; i++)
                        {
                            int pid = Marshal.ReadInt32(rowPtr, 24);
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

        _cachedEstablishedSockets = established;
        _cachedTotalSockets = total;
        return (established, total);
    }

    private static int ParsePidFromGpuInstance(ReadOnlySpan<char> inst)
    {
        int pidStart = inst.IndexOf("pid_");
        if (pidStart < 0) return -1;
        var slice = inst.Slice(pidStart + 4);
        int pidEnd = slice.IndexOf('_');
        if (pidEnd <= 0) return -1;
        return int.TryParse(slice.Slice(0, pidEnd), out int pid) ? pid : -1;
    }

    private static bool IsSupportedGpuEngine(ReadOnlySpan<char> inst)
    {
        return inst.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase) ||
               inst.Contains("engtype_Compute", StringComparison.OrdinalIgnoreCase) ||
               inst.Contains("engtype_VideoDecode", StringComparison.OrdinalIgnoreCase) ||
               inst.Contains("engtype_VideoEncode", StringComparison.OrdinalIgnoreCase) ||
               inst.Contains("engtype_VideoProcessing", StringComparison.OrdinalIgnoreCase) ||
               inst.Contains("engtype_Copy", StringComparison.OrdinalIgnoreCase) ||
               inst.Contains("engtype_VR", StringComparison.OrdinalIgnoreCase);
    }

    private void PollGpuCounters(Dictionary<int, ProcessItem> processMap)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (!_gpuCategoriesChecked || (now - _lastGpuCategoryCheck).TotalSeconds > 30)
            {
                _lastGpuCategoryCheck = now;
                _gpuCategoriesChecked = true;
                try
                {
                    if (_gpuEngineCategory == null && PerformanceCounterCategory.Exists("GPU Engine"))
                        _gpuEngineCategory = new PerformanceCounterCategory("GPU Engine");
                }
                catch { _gpuEngineCategory = null; }

                try
                {
                    if (_gpuMemoryCategory == null && PerformanceCounterCategory.Exists("GPU Process Memory"))
                        _gpuMemoryCategory = new PerformanceCounterCategory("GPU Process Memory");
                }
                catch { _gpuMemoryCategory = null; }
            }

            if (_gpuEngineCategory == null)
            {
                GpuEngineState = GpuProcessEngineState.CountersDisabled;
                return;
            }

            // 1. Single-call batch read of GPU Engine Utilization Percentage (~5ms vs 506ms per-instance NextValue)
            var catData = _gpuEngineCategory.ReadCategory();
            if (catData.Contains("Utilization Percentage"))
            {
                var utilCol = catData["Utilization Percentage"];
                int totalInstances = utilCol.Count;
                int supportedInstances = 0;
                _currGpuSamples.Clear();

                foreach (InstanceData id in utilCol.Values)
                {
                    ReadOnlySpan<char> instSpan = id.InstanceName.AsSpan();
                    if (!instSpan.Contains("pid_", StringComparison.Ordinal) || !IsSupportedGpuEngine(instSpan))
                        continue;

                    supportedInstances++;
                    if (_prevGpuSamples.TryGetValue(id.InstanceName, out var prev))
                    {
                        int pid = prev.Pid;
                        float val = CounterSample.Calculate(prev.Sample, id.Sample);
                        if (val > 0.01f && float.IsFinite(val) && processMap.TryGetValue(pid, out var item))
                        {
                            item.GpuPercent = Math.Min(100f, item.GpuPercent + val);
                        }
                        _currGpuSamples[id.InstanceName] = (id.Sample, pid);
                    }
                    else
                    {
                        int pid = ParsePidFromGpuInstance(instSpan);
                        if (pid > 0)
                        {
                            _currGpuSamples[id.InstanceName] = (id.Sample, pid);
                        }
                    }
                }

                (_prevGpuSamples, _currGpuSamples) = (_currGpuSamples, _prevGpuSamples);

                if (totalInstances == 0)
                    GpuEngineState = GpuProcessEngineState.CountersDisabled;
                else if (supportedInstances == 0)
                    GpuEngineState = GpuProcessEngineState.NoSupportedEngines;
                else
                    GpuEngineState = GpuProcessEngineState.Active;
            }
            else
            {
                GpuEngineState = GpuProcessEngineState.CountersDisabled;
            }

            // 2. Single-call batch read of GPU Process Memory (<1ms) for Dedicated / Local VRAM per process
            if (_gpuMemoryCategory != null)
            {
                try
                {
                    var memData = _gpuMemoryCategory.ReadCategory();
                    InstanceDataCollection? memCol = null;
                    if (memData.Contains("Dedicated Usage"))
                        memCol = memData["Dedicated Usage"];
                    else if (memData.Contains("Local Usage"))
                        memCol = memData["Local Usage"];

                    if (memCol != null)
                    {
                        foreach (InstanceData id in memCol.Values)
                        {
                            long bytes = id.RawValue;
                            if (bytes <= 0) continue;
                            int pid = ParsePidFromGpuInstance(id.InstanceName.AsSpan());
                            if (pid > 0 && processMap.TryGetValue(pid, out var item))
                            {
                                item.GpuVramMb += bytes / (1024f * 1024f);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticRingBuffer.Log("ProcessTracker:GpuMemRead", ex);
                }
            }
        }
        catch (Exception ex)
        {
            GpuEngineState = GpuProcessEngineState.Failed;
            DiagnosticRingBuffer.Log("ProcessTracker:PollGpuCounters", ex);
        }
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
