using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Clocky.Core;

public class NetworkTracker : IDisposable
{
    private class InterfaceState
    {
        public string Id { get; set; } = string.Empty;
        public long LastBytesReceived { get; set; }
        public long LastBytesSent { get; set; }
        public DateTime LastSampleTime { get; set; }
        public float CurrentDownSpeedKBps { get; set; }
        public float CurrentUpSpeedKBps { get; set; }
        public List<float> DownloadHistory { get; } = new();
        public List<float> UploadHistory { get; } = new();
    }

    private readonly Dictionary<string, InterfaceState> _states = new();
    private DateTime _lastPollTime = DateTime.UtcNow;
    private const int MaxSparklinePoints = 40;

    public bool DetailedMode { get; set; } = false;
    private List<NetworkInterfaceTelemetry> _lastDetailedInterfaces = new();
    private string _lastPrimaryName = "No Network";
    private string _lastPrimaryIp = "";

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

    private static int GetTargetIfIndex()
    {
        try
        {
            uint dest = BitConverter.ToUInt32(IPAddress.Parse("8.8.8.8").GetAddressBytes(), 0);
            if (GetBestInterface(dest, out uint idx) == 0)
                return (int)idx;
        }
        catch { }
        return -1;
    }

    public (List<NetworkInterfaceTelemetry> Interfaces, float TotalDownKBps, float TotalUpKBps, ulong TotalBytesRecv, ulong TotalBytesSent, string PrimaryNetName, string PrimaryIp) Poll()
    {
        var now = DateTime.UtcNow;
        double globalDeltaSec = (now - _lastPollTime).TotalSeconds;
        if (globalDeltaSec <= 0.001) globalDeltaSec = 1.0;
        _lastPollTime = now;

        var resultList = new List<NetworkInterfaceTelemetry>();
        float totalDownKBps = 0f;
        float totalUpKBps = 0f;
        ulong totalBytesRecv = 0;
        ulong totalBytesSent = 0;
        string primaryName = "No Network";
        string primaryIp = "";
        int bestIfIndex = GetTargetIfIndex();
        int highestPrimaryScore = -1;

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();

            foreach (var nic in interfaces)
            {
                // Skip software loopback
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                // Skip NDIS lightweight filter miniports and packet scheduler clones
                if (IsSubFilterDriver(nic)) continue;

                var ipProps = nic.GetIPProperties();
                var stats = nic.GetIPStatistics();

                long curRecv = stats.BytesReceived;
                long curSent = stats.BytesSent;

                totalBytesRecv += (ulong)Math.Max(0, curRecv);
                totalBytesSent += (ulong)Math.Max(0, curSent);

                if (!_states.TryGetValue(nic.Id, out var state))
                {
                    state = new InterfaceState
                    {
                        Id = nic.Id,
                        LastBytesReceived = curRecv,
                        LastBytesSent = curSent,
                        LastSampleTime = now
                    };
                    _states[nic.Id] = state;
                }

                double deltaSec = (now - state.LastSampleTime).TotalSeconds;
                if (deltaSec <= 0.001) deltaSec = globalDeltaSec;

                long diffRecv = curRecv - state.LastBytesReceived;
                long diffSent = curSent - state.LastBytesSent;

                state.LastBytesReceived = curRecv;
                state.LastBytesSent = curSent;
                state.LastSampleTime = now;

                float downKBps = 0f;
                float upKBps = 0f;

                if (diffRecv >= 0 && deltaSec > 0)
                {
                    downKBps = (float)((diffRecv / 1024.0) / deltaSec);
                }
                if (diffSent >= 0 && deltaSec > 0)
                {
                    upKBps = (float)((diffSent / 1024.0) / deltaSec);
                }

                state.CurrentDownSpeedKBps = downKBps;
                state.CurrentUpSpeedKBps = upKBps;

                state.DownloadHistory.Add(downKBps);
                if (state.DownloadHistory.Count > MaxSparklinePoints) state.DownloadHistory.RemoveAt(0);

                state.UploadHistory.Add(upKBps);
                if (state.UploadHistory.Count > MaxSparklinePoints) state.UploadHistory.RemoveAt(0);

                // Add to aggregate totals if interface is operational
                if (nic.OperationalStatus == OperationalStatus.Up)
                {
                    totalDownKBps += downKBps;
                    totalUpKBps += upKBps;
                }

                // If not in detailed mode (Network tab closed) and we already have cached details, skip rebuilding
                if (!DetailedMode && _lastDetailedInterfaces.Count > 0) continue;

                // Extract IP information
                string ipv4 = "";
                string ipv6 = "";
                foreach (var u in ipProps.UnicastAddresses)
                {
                    if (u.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        if (string.IsNullOrEmpty(ipv4)) ipv4 = u.Address.ToString();
                    }
                    else if (u.Address.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        if (string.IsNullOrEmpty(ipv6) && !u.Address.IsIPv6LinkLocal) ipv6 = u.Address.ToString();
                    }
                }
                if (string.IsNullOrEmpty(ipv6))
                {
                    var linkLocal = ipProps.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6);
                    if (linkLocal != null) ipv6 = linkLocal.Address.ToString();
                }

                // Gateway & DNS
                string gateway = ipProps.GatewayAddresses.FirstOrDefault()?.Address.ToString() ?? "";
                var dnsList = ipProps.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork).Select(d => d.ToString()).ToList();
                string dnsStr = dnsList.Count > 0 ? string.Join(", ", dnsList) : "";

                // Formatted MAC
                var macBytes = nic.GetPhysicalAddress().GetAddressBytes();
                string mac = macBytes.Length > 0 ? string.Join(":", macBytes.Select(b => b.ToString("X2"))) : "";

                // Link Speed
                long rawSpeed = nic.Speed;
                string speedStr = rawSpeed > 0 ? FormatLinkSpeed(rawSpeed) : "Unknown Link";

                // Type Descriptor
                string typeDesc = GetInterfaceTypeString(nic);
                bool isUp = nic.OperationalStatus == OperationalStatus.Up;

                var item = new NetworkInterfaceTelemetry
                {
                    Id = nic.Id,
                    Name = nic.Name,
                    Description = nic.Description,
                    InterfaceType = typeDesc,
                    IsUp = isUp,
                    Status = isUp ? "Connected" : "Disconnected",
                    SpeedMbps = rawSpeed > 0 ? (float)(rawSpeed / 1_000_000.0) : 0f,
                    SpeedFormatted = speedStr,
                    Ipv4Address = ipv4,
                    Ipv6Address = ipv6,
                    Gateway = gateway,
                    Dns = dnsStr,
                    MacAddress = mac,
                    DownloadSpeedKBps = downKBps,
                    UploadSpeedKBps = upKBps,
                    DownloadSpeedFormatted = FormatSpeed(downKBps),
                    UploadSpeedFormatted = FormatSpeed(upKBps),
                    TotalBytesReceived = (ulong)Math.Max(0, curRecv),
                    TotalBytesSent = (ulong)Math.Max(0, curSent),
                    FormattedTotalReceived = FormatBytes((ulong)Math.Max(0, curRecv)),
                    FormattedTotalSent = FormatBytes((ulong)Math.Max(0, curSent)),
                    DownloadHistory = new List<float>(state.DownloadHistory),
                    UploadHistory = new List<float>(state.UploadHistory)
                };

                resultList.Add(item);

                // Determine primary network using kernel routing & gateway scoring
                int ifIdx = -1;
                try { ifIdx = ipProps.GetIPv4Properties()?.Index ?? -1; } catch { }

                bool isVirtual = typeDesc.Contains("VPN", StringComparison.OrdinalIgnoreCase) ||
                                 typeDesc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                                 nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                                 nic.Description.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase) ||
                                 nic.Description.Contains("Tailscale", StringComparison.OrdinalIgnoreCase);

                int primaryScore = 0;
                if (ifIdx > 0 && ifIdx == bestIfIndex) primaryScore += 1000;
                if (!string.IsNullOrEmpty(gateway) && !gateway.StartsWith("0.0.0.0")) primaryScore += 500;
                if (!isVirtual) primaryScore += 200;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet || nic.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet) primaryScore += 50;
                else if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) primaryScore += 40;
                if (!string.IsNullOrEmpty(ipv4)) primaryScore += 10;

                if (isUp && !string.IsNullOrEmpty(ipv4) && !ipv4.StartsWith("169.254") && primaryScore > highestPrimaryScore)
                {
                    highestPrimaryScore = primaryScore;
                    primaryName = $"{nic.Name} ({typeDesc})";
                    primaryIp = ipv4;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Clocky] Network poll error: {ex.Message}");
        }

        if (!DetailedMode && _lastDetailedInterfaces.Count > 0)
        {
            return (_lastDetailedInterfaces, totalDownKBps, totalUpKBps, totalBytesRecv, totalBytesSent, _lastPrimaryName, _lastPrimaryIp);
        }

        // Sort interfaces: Connected first, then by total cumulative throughput (BytesRecv + BytesSent), then by name
        resultList = resultList
            .OrderByDescending(n => n.IsUp)
            .ThenByDescending(n => n.TotalBytesReceived + n.TotalBytesSent)
            .ThenBy(n => n.Name)
            .ToList();

        _lastDetailedInterfaces = resultList;
        _lastPrimaryName = primaryName;
        _lastPrimaryIp = primaryIp;

        return (resultList, totalDownKBps, totalUpKBps, totalBytesRecv, totalBytesSent, primaryName, primaryIp);
    }

    private static bool IsSubFilterDriver(NetworkInterface nic)
    {
        string name = nic.Name;
        string desc = nic.Description;

        // Skip sub-filters created by Npcap, QoS, WFP, VirtualBox, etc. on physical NICs
        if (name.Contains("-Npcap", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("-QoS", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("-WFP", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("-VirtualBox", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("-Native WiFi", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("-Virtual WiFi", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (desc.Contains("Filter Driver", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("Packet Scheduler", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("LightWeight Filter", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("Light-Weight Filter", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("NPCAP Packet Driver", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("Kernel Debugger", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("Pseudo-Interface", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("6to4 Adapter", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("IP-HTTPS", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("Teredo", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string GetInterfaceTypeString(NetworkInterface nic)
    {
        string nameLower = nic.Name.ToLowerInvariant();
        string descLower = nic.Description.ToLowerInvariant();

        if (descLower.Contains("tailscale") || nameLower.Contains("tailscale")) return "Tailscale VPN";
        if (descLower.Contains("zerotier") || nameLower.Contains("zerotier")) return "ZeroTier VPN";
        if (descLower.Contains("wireguard") || nameLower.Contains("wireguard") || descLower.Contains("openvpn")) return "VPN Tunnel";
        if (descLower.Contains("virtualbox") || descLower.Contains("hyper-v") || descLower.Contains("vmware") || descLower.Contains("wsl")) return "Virtual Adapter";

        return nic.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => "Wi-Fi",
            NetworkInterfaceType.Ethernet => "Ethernet",
            NetworkInterfaceType.GigabitEthernet => "Gigabit Ethernet",
            NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT => "Fast Ethernet",
            NetworkInterfaceType.Ppp => "PPP Connection",
            NetworkInterfaceType.Tunnel => "Tunnel Adapter",
            _ => "Network Adapter"
        };
    }

    private static string FormatLinkSpeed(long bitsPerSec)
    {
        var inv = CultureInfo.InvariantCulture;
        if (bitsPerSec >= 1_000_000_000)
            return $"{(bitsPerSec / 1_000_000_000.0).ToString("0.#", inv)} Gbps";
        if (bitsPerSec >= 1_000_000)
            return $"{(bitsPerSec / 1_000_000.0).ToString("0.#", inv)} Mbps";
        if (bitsPerSec >= 1_000)
            return $"{(bitsPerSec / 1_000.0).ToString("0.#", inv)} Kbps";
        return $"{bitsPerSec} bps";
    }

    public static string FormatSpeed(float kbps)
    {
        var inv = CultureInfo.InvariantCulture;
        if (kbps >= 1024f * 1024f)
            return $"{(kbps / (1024f * 1024f)).ToString("0.00", inv)} GB/s";
        if (kbps >= 1024f)
            return $"{(kbps / 1024f).ToString("0.0", inv)} MB/s";
        if (kbps >= 1f)
            return $"{kbps.ToString("0.0", inv)} KB/s";
        if (kbps > 0f)
            return $"{kbps.ToString("0.00", inv)} KB/s";
        return "0.0 KB/s";
    }

    public static string FormatBytes(ulong bytes)
    {
        var inv = CultureInfo.InvariantCulture;
        double b = bytes;
        if (b >= 1024.0 * 1024.0 * 1024.0 * 1024.0)
            return $"{(b / (1024.0 * 1024.0 * 1024.0 * 1024.0)).ToString("0.00", inv)} TB";
        if (b >= 1024.0 * 1024.0 * 1024.0)
            return $"{(b / (1024.0 * 1024.0 * 1024.0)).ToString("0.1", inv)} GB";
        if (b >= 1024.0 * 1024.0)
            return $"{(b / (1024.0 * 1024.0)).ToString("0.1", inv)} MB";
        if (b >= 1024.0)
            return $"{(b / 1024.0).ToString("0", inv)} KB";
        return $"{b.ToString("0", inv)} B";
    }

    public void Dispose()
    {
        _states.Clear();
    }
}
