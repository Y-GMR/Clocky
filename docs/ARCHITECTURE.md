# Clocky Architecture Specification

## 1. System Overview

Clocky is a Windows hardware telemetry and observability system built with .NET 9 and Windows Presentation Foundation (WPF). The application operates via a decoupled architecture separating low-level hardware ingestion, data normalization, asynchronous process tracking, and Direct3D-accelerated user interface rendering.

```text
+-------------------------------------------------------------------------------+
|                             Kernel / Ring-0 Layer                             |
|        WinRing0 Driver / NVML DLL / DXGI Adapter / Windows Kernel ETW         |
+-------------------------------------------------------------------------------+
                                       │
                                       ▼
+-------------------------------------------------------------------------------+
|                       Ingestion Engine (HardwareEngine)                       |
|           Threadpool Poller -> Sensor Normalization -> TelemetrySnapshot      |
+-------------------------------------------------------------------------------+
                                       │
                    ┌──────────────────┴──────────────────┐
                    ▼                                     ▼
+------------------------------------+  +------------------------------------+
|        WPF Rendering Pipeline      |  |     Win32 System Tray Engine       |
| Direct3D Composition / Waveforms   |  | Dedicated HWNDs / ClockyTrayIcon   |
+------------------------------------+  +------------------------------------+
```

---

## 2. Hardware Ingestion Pipeline

### Driver Interfacing
- **LibreHardwareMonitorLib**: Manages the `WinRing0` kernel driver to read Model-Specific Registers (MSR), Embedded Controller (EC) offsets, and SMBus sensor lines.
- **NVML Interop**: Direct C-interop binding with `nvml.dll` to query NVIDIA GPU core utilization, VRAM allocation, temperature, and board power draw.
- **DXGI / Performance Counters**: Captures GPU 3D, Compute, Copy, and Video Decoding/Encoding engine utilizations across multi-GPU environments.
- **Network Interface Statistics**: Samples native `NetworkInterface.GetIPv4IPv6Statistics` and tracks delta transfer rates per adapter.

### Sampling Pipeline (`HardwareEngine.cs`)
Telemetry sampling executes on a dedicated background thread pool worker to isolate kernel driver latency from the UI thread:
1. `IComputer.Accept(IVisitor)` executes hardware updates across CPU, GPU, Memory, Storage, Motherboard, and Controller nodes.
2. Raw sensor values are normalized and packaged into an immutable `TelemetrySnapshot` struct.
3. The snapshot is dispatched asynchronously via `TelemetryUpdated` event handlers to the UI and system tray managers.

---

## 3. Telemetry Storage & Bounded History Buffers

To maintain low GC latency, temporal history series use bounded, fixed-capacity list buffers (60 samples):

```csharp
private static void PushHistory(List<float> history, float value)
{
    while (history.Count >= 60)
    {
        history.RemoveAt(0);
    }
    history.Add(value);
}
```

Snapshot references are replaced atomically on the UI thread without re-allocating sensor array collections.

---

## 4. UI Rendering & Waveform Pipeline

### Vector Waveform Rasterization
- Waveforms are rendered into WPF `Canvas` controls using `Polyline` and `Polygon` geometry.
- Graph bounding limits are calculated with cached max evaluations (`CachedMaxSampleCount`), preventing O(n) history scans on mouse-move events.
- Sub-pixel cursor throttling prevents redundant layout recalculations during cursor movement over active waveforms.

### Hybrid CPU Topology Mapping
- CPU core topology is resolved dynamically on initialization:
  - **Heterogeneous / Hybrid Intel**: Directly queries the Windows NT kernel scheduler via Win32 `GetLogicalProcessorInformationEx` (`RelationProcessorCore`) in `CpuTopologyHelper`, extracting physical core records, `EfficiencyClass` (0 = E-core, 1+ = P-core), SMT flags, and processor affinity bitmasks. Caches hybrid layouts into Performance Core (`GridPCores`) and Efficiency Core (`GridECores`) matrices without heuristic thread-count thresholds or SKU string matching.
  - **AMD Ryzen / Uniform**: Dynamically sizes multi-die CCX/CCD thread grids based on kernel physical core and logical thread topology.

---

## 5. Win32 System Tray Architecture

Standard WinForms `NotifyIcon` implementations assign a shared HWND with `uID = 1`, causing icon grouping and drag target collisions in Windows 11 taskbars.

Clocky implements a native Win32 `ClockyTrayIcon` wrapper:
- **Dedicated Message Windows**: Each tray icon allocates an independent `HwndSource` message window.
- **Unique Shell Identifiers**: Each sensor badge is assigned a unique `uID` offset (`uID = 200 + sensor.Order * 10`).
- **P/Invoke Notification Interface**: Dispatches `Shell_NotifyIcon` with `NOTIFYICON_VERSION_4` flags (`NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP`).
- **Window Restoration**: Uses `ShowWindow(hWnd, SW_RESTORE)` and `SetForegroundWindow(hWnd)` to reliably un-minimize the main window across virtual desktops.

---

## 6. Process & Bandwidth Attribution Engine

- **ProcessTracker**: Samples top CPU, GPU (3D and dedicated VRAM engines), and RAM consumers, while tracking Disk I/O bytes via `NtQuerySystemInformation`. State dictionaries are keyed by composite `(int Pid, long CreateTime)` tuples extracted directly from `SYSTEM_PROCESS_INFORMATION.CreateTime`, preventing counter corruption across rapid Windows PID recycling. Unoccupied leaderboard slots collapse cleanly without artificial RAM padding.
- **Kernel ETW Network Bandwidth Accounting**: Hooks `Microsoft-Windows-TCPIP` (`NetworkTCPIP`) ETW kernel trace events in real time to capture exact per-PID download and upload throughput deltas. Proactively reclaims any orphaned sessions on startup.
- **Native Socket State Polling**: Employs `iphlpapi.dll` (`GetExtendedTcpTable` / `GetExtendedUdpTable`) for point-in-time socket table enumeration to track active and established socket connection counts per PID.

---

## 7. Diagnostics & Exception Handling

- **Global Exception Interceptor**: Hooks `AppDomain.UnhandledException`, `DispatcherUnhandledException`, and `TaskScheduler.UnobservedTaskException`.
- **Structured Crash Logging**: Serializes crash context, stack trace, loaded modules, and hardware state into timestamped logs.
- **Modal Reporting Interface**: Displays a dedicated diagnostic dialog (`ErrorReportWindow.xaml`) enabling 1-click clipboard reproduction payloads.

---

## 8. Versioning & Release Automation

Clocky uses Semantic Versioning (`MAJOR.MINOR.PATCH`):
- **MAJOR**: Breaking configuration schema shifts, driver architecture redesigns, or OS platform requirement changes.
- **MINOR**: Additions of new telemetry sensors, UI tabs, or hardware vendor detection rules.
- **PATCH**: Hotfixes, performance throttling adjustments, and bug fixes.

Release workflows in `.github/workflows/release.yml` trigger on Git tag pushes matching `v*.*.*`, compiling single-file binaries (`dist/Clocky.exe`) and updating the update manifest `version.json`.