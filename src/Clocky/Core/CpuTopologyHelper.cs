using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Clocky.Core;

public class CpuCoreInfo
{
    public int CoreIndex { get; set; }
    public byte EfficiencyClass { get; set; }
    public bool IsSmt { get; set; }
    public string CoreType { get; set; } = "Core";
    public List<int> LogicalProcessorIndices { get; set; } = new();
}

public class CpuTopology
{
    public bool IsHeterogeneous { get; set; }
    public int PhysicalCoreCount { get; set; }
    public int LogicalProcessorCount { get; set; }
    public int PCoreCount { get; set; }
    public int ECoreCount { get; set; }
    public List<CpuCoreInfo> Cores { get; set; } = new();
    public Dictionary<int, CpuCoreInfo> ThreadToCoreMap { get; set; } = new();
}

public static class CpuTopologyHelper
{
    private const int RelationProcessorCore = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(
        int relationshipType,
        IntPtr buffer,
        ref int returnedLength);

    private static CpuTopology? _cachedTopology;
    private static readonly object _lock = new();

    public static CpuTopology GetTopology()
    {
        if (_cachedTopology != null) return _cachedTopology;

        lock (_lock)
        {
            if (_cachedTopology != null) return _cachedTopology;
            _cachedTopology = QueryTopology();
            return _cachedTopology;
        }
    }

    private static CpuTopology QueryTopology()
    {
        var topology = new CpuTopology();
        int length = 0;

        // Query required buffer length
        GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length);
        if (length == 0)
        {
            return FallbackTopology();
        }

        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
            {
                return FallbackTopology();
            }

            IntPtr current = buffer;
            int offset = 0;
            int coreIndex = 0;

            while (offset < length)
            {
                int relationship = Marshal.ReadInt32(current, 0);
                int size = Marshal.ReadInt32(current, 4);

                if (size <= 0) break;

                if (relationship == RelationProcessorCore)
                {
                    byte flags = Marshal.ReadByte(current, 8);
                    byte effClass = Marshal.ReadByte(current, 9);
                    short groupCount = Marshal.ReadInt16(current, 30);

                    bool isSmt = (flags & 1) != 0;
                    var core = new CpuCoreInfo
                    {
                        CoreIndex = coreIndex,
                        EfficiencyClass = effClass,
                        IsSmt = isSmt
                    };

                    // GroupMask starts at offset 32 (8-byte aligned on x64)
                    int maskOffset = 32;
                    int actualGroups = Math.Max(1, (int)groupCount);

                    for (int g = 0; g < actualGroups; g++)
                    {
                        if (offset + maskOffset + 16 > length) break;

                        ulong mask = (ulong)Marshal.ReadInt64(current, maskOffset);
                        ushort group = (ushort)Marshal.ReadInt16(current, maskOffset + 8);
                        int baseThreadIdx = group * 64;

                        for (int bit = 0; bit < 64; bit++)
                        {
                            if ((mask & (1UL << bit)) != 0)
                            {
                                int threadIdx = baseThreadIdx + bit;
                                core.LogicalProcessorIndices.Add(threadIdx);
                                topology.ThreadToCoreMap[threadIdx] = core;
                            }
                        }

                        maskOffset += 16; // sizeof(GROUP_AFFINITY) = 8 + 2 + 6 = 16 bytes
                    }

                    topology.Cores.Add(core);
                    coreIndex++;
                }

                current = IntPtr.Add(current, size);
                offset += size;
            }

            topology.PhysicalCoreCount = topology.Cores.Count;
            topology.LogicalProcessorCount = topology.ThreadToCoreMap.Count > 0 
                ? topology.ThreadToCoreMap.Count 
                : Environment.ProcessorCount;

            // Determine if heterogeneous
            var distinctClasses = topology.Cores.Select(c => c.EfficiencyClass).Distinct().OrderBy(c => c).ToList();
            if (distinctClasses.Count > 1)
            {
                topology.IsHeterogeneous = true;
                byte minEff = distinctClasses.First();

                foreach (var core in topology.Cores)
                {
                    if (core.EfficiencyClass > minEff)
                    {
                        core.CoreType = "P-Core";
                        topology.PCoreCount++;
                    }
                    else
                    {
                        core.CoreType = "E-Core";
                        topology.ECoreCount++;
                    }
                }
            }
            else
            {
                topology.IsHeterogeneous = false;
                topology.PCoreCount = topology.PhysicalCoreCount;
                topology.ECoreCount = 0;
                foreach (var core in topology.Cores)
                {
                    core.CoreType = "Core";
                }
            }

            return topology;
        }
        catch
        {
            return FallbackTopology();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static CpuTopology FallbackTopology()
    {
        var topology = new CpuTopology();
        int threadCount = Math.Max(1, Environment.ProcessorCount);
        topology.PhysicalCoreCount = threadCount;
        topology.LogicalProcessorCount = threadCount;
        topology.PCoreCount = threadCount;
        topology.ECoreCount = 0;
        topology.IsHeterogeneous = false;

        for (int i = 0; i < threadCount; i++)
        {
            var core = new CpuCoreInfo
            {
                CoreIndex = i,
                EfficiencyClass = 0,
                IsSmt = false,
                CoreType = "Core",
                LogicalProcessorIndices = new List<int> { i }
            };
            topology.Cores.Add(core);
            topology.ThreadToCoreMap[i] = core;
        }

        return topology;
    }
}
