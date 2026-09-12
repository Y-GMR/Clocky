# Changelog

All notable changes to Clocky are documented in this file.

The format is based on Keep a Changelog, and this project adheres to Semantic Versioning.

## [1.3.1] - 2026-09-12

### Added
- Provenance and fallback tracking (`IsFallback`, `Provenance`) in `CpuTopologyHelper.CpuTopology`, explicitly tagging synthetic fallback approximations if native topology queries fail.
- AMD Zen CPU package temperature matching for `Core (Tctl/Tdie)`, `Tctl/Tdie`, `Tdie`, `Tctl`, `CPU CCD1`, and fallback ACPI thermal zone polling (`Win32_PerfFormattedData_Counters_ThermalZoneInformation`).
- GPU video and copy engine performance counter tracking (`engtype_VideoDecode`, `engtype_VideoEncode`, `engtype_VideoProcessing`, `engtype_Copy`) in `ProcessTracker.PollGpuCounters`.
- Full hardware model name tooltips on navigation tabs (`NavCpuText`, `NavGpuText`) and character ellipsis trimming (`TextTrimming="CharacterEllipsis"`).

### Changed
- Refactored `SystemHardwareHelper.GetShortCpuName` to strip integrated GPU marketing suffixes (e.g. `with Radeon ... Graphics`, `Intel Arc Graphics`) and enforce 20-character bounds with ellipsis.
- Refactored `SystemHardwareHelper.GetShortGpuName` to truncate at 16 characters with ellipsis.

### Fixed
- Fixed AMD Zen core sensor matching in `HardwareEngine` by updating core regex to `@"^(?:CPU\s+)?Core\s*#?(\d+)$"`, resolving empty per-core telemetry on Ryzen processors.
- Fixed sensor provenance alignment between UI headline telemetry and All Sensors matrix, eliminating provenance mismatches for CPU Package Temp, Vcore, and Core Max.
- Enforced Rule 5 zero synthetic fallbacks by removing GPU Hotspot <- Memory Temp cross-substitution, GPU 3D Util <-> Core Util fallback, and synthetic multi-core clock replication in `SystemHardwareHelper.GetProcessorClocks`.
- Fixed resource leak in `HardwareEngine.Dispose()` by deterministically invoking `_processTracker.Dispose()`.

---

## [1.3.0] - 2026-09-05

### Added
- Global Sensor Provenance Architecture (`SensorProvenance`): every sensor and telemetry metric across CPU thermals, RAPL power rails, core frequencies, voltages, GPU silicon, memory, battery, storage, and network is explicitly tagged with its authoritative source (`NativeMSR`, `DriverLHM`, `MotherboardSuperIO`, `KernelETW`, `PerformanceCounter`, `FallbackApproximation`, `Unavailable`).
- Source column in All Sensors Matrix displaying sensor provenance labels, supporting interactive sorting and filter queries (e.g. searching "MSR", "LHM", "ETW", or "SuperIO").
- Expandable child process grouping in Process Observability Matrix: selecting grouped multi-instance process rows (e.g. browsers, IDE workers) expands inline row details displaying individual child PIDs with per-process CPU, GPU, RAM, and status telemetry.
- Bounded thread-safe in-memory diagnostic buffer (`DiagnosticRingBuffer`, 200 events) recording swallowed sensor exceptions, driver queries, and API errors.
- Real-time GPU engine status badge (`BrdGpuEngineStatus`) in Process Observability Matrix notifying users if Windows GPU performance counters are inactive, missing, or uninitialized instead of silently rendering 0%.
- Native in-process Task Scheduler COM registration via `Schedule.Service`, creating and managing elevated logon tasks with zero CLI process spawning and zero temporary disk files.

### Changed
- Refactored `StartupHelper` to prioritize native Task Scheduler COM registration before falling back to `schtasks.exe`.
- Added provenance tooltips to CPU package temperature and voltage telemetry displays in CPU Observability view.
- Pre-cached physical core arrays (`PCores`, `ECores`) in `CpuTopologyHelper` and pre-compiled CPU sensor regexes, eliminating periodic LINQ allocations and string matching overhead in `HardwareEngine`.
- Optimized `ProcessTracker` GPU counter cache by pairing counters directly with parsed process IDs and eliminated multi-instance grouping allocations via shared empty buffers and single-pass sorting.

### Fixed
- Added deterministic COM RCW cleanup via `Marshal.ReleaseComObject` in `StartupHelper` to prevent Task Scheduler COM handle retention.
- Added conditional suppression of row detail layout artifacts in Process Observability Matrix for single-instance processes.
- Fixed `run_clocky.bat` relative path resolution to search root `dist/` directory before build output directories.

---

## [1.2.1] - 2026-09-05

### Fixed
- Fixed auto-updater sharing violation crash (`ERROR_SHARING_VIOLATION` / `0x80070020`) in `UpdateManager.DownloadUpdateAsync` where `using var verifyStream` held an unclosed file handle on the downloaded binary during atomic file promotion (`File.Move`).

---

## [1.2.0] - 2026-09-05

### Added
- Native Windows kernel CPU topology extraction via `GetLogicalProcessorInformationEx` (`RelationProcessorCore`) in `CpuTopologyHelper`, directly resolving physical cores, `EfficiencyClass` (P-core vs E-core), SMT flags, and logical processor bitmasks without thread-count heuristics or CPU SKU string pattern matching.
- Explicit electrical sensor distinction between native CPU MSR VID (`CPU Core VID`) and motherboard SuperIO/VRM voltage (`Motherboard Vcore`).

### Changed
- Refactored `ProcessTracker` sample caching to key previous measurements by `(int Pid, long CreateTime)` from `SYSTEM_PROCESS_INFORMATION.CreateTime`, eliminating accounting delta errors across rapid Windows PID reuse.
- Updated CPU overview and vital cards to render `— GHz` or `— °C` when individual core telemetry is unavailable, enforcing strict zero synthetic fallbacks.

### Fixed
- Fixed misleading Top-3 leaderboard padding where idle applications were injected into Top Disk I/O and Top Network lists based on RAM consumption (`WorkingSetBytes`).
- Fixed multi-threaded timer race condition in `HardwareEngine.Poll` by replacing non-atomic boolean flag with atomic `Interlocked.Exchange`.
- Added synchronous timeout join (`_etwTask?.Wait(500)`) on background ETW trace processing task during `ProcessTracker.Dispose`.

---

## [1.1.3] - 2026-09-04

### Security
- Fixed `ClockyTestServer` Origin and Referer validation by replacing string prefix matching with strict absolute URI host verification (`127.0.0.1`, `localhost`, `[::1]`), preventing lookalike domain bypasses (`localhost.attacker.com`).
- Enforced mandatory cryptographic SHA256 checksum verification during update downloads, rejecting and deleting binaries if the update manifest omits hash metadata.

### Fixed
- Fixed Windows startup failure on system restart by migrating from legacy HKCU Run registry keys (which silently suppress elevated binaries) to native elevated Windows Task Scheduler tasks (`HighestAvailable` runlevel on user logon).
- Hardened auto-updater rollback in `apply_update.ps1` by verifying the replacement executable launches and maintains stability for 3 seconds before discarding backup copies.
- Added proactive ETW kernel session reclamation in `ProcessTracker` to detect and stop orphaned `ClockyKernelNetTrace` sessions left by prior ungraceful exits.

---

## [1.1.2] - 2026-08-30

### Fixed
- Fixed DataGrid column resizing snapping on All Sensors Matrix and Processes & Apps tables by replacing Star sizing (`Width="*"`) with explicit pixel dimensions in horizontal scroll containers.
- Resolved column collapse and layout redistribution during 1-second telemetry background data refreshes.
- Normalized per-process CPU utilization by logical processor count to match Windows Task Manager and Process Lasso total system capacity metrics.
- Persisted user-resized column widths to `clocky_config.json` so custom widths are restored after restart.

---

## [1.1.1] - 2026-08-30

### Fixed
- Fixed auto-updater payload truncation vulnerability by enforcing atomic `.tmp` staging, stream completion checks, and minimum binary size validation.
- Added automatic transactional rollback in `apply_update.ps1` to create `.bak` executable backups and restore known-good binaries if replacement fails.

---

## [1.1.0] - 2026-08-30

### Added
- Real-time Kernel ETW network packet accounting via `Microsoft.Diagnostics.Tracing.TraceEvent` for per-process throughput, combined with native `iphlpapi.dll` table polling for socket counts.
- 5th top-leaderboard card in Processes & Apps view displaying top network I/O processes with independent upload/download speeds.
- Test server API endpoints (`/api/table/columns`, `/api/table/resize`, `/api/exit`) for programmatic DataGrid inspection and column resizing.

### Changed
- Stacked metric and progress bar layout for Disk I/O and Net I/O leaderboard cards for improved legibility.
- Enabled `ScrollViewer.HorizontalScrollBarVisibility="Auto"` across DataGrid tables to prevent viewport clipping and column snapping during width adjustments.
- Unified search filter styling across All Sensors Matrix (`TxtSensorFilter`) and Processes & Apps (`TxtProcessFilter`) with uniform rounded containers.

### Fixed
- Fixed DataGrid header gripper hit-testing by adjusting Z-index and margins to prevent column resize gestures from inadvertently triggering column sorting.
- Aligned DataGrid property configurations on `GridAllSensors` (`IsReadOnly`, `CanUserAddRows`, `CanUserDeleteRows`, `HeadersVisibility`, `RowHeight`, `SelectionUnit`).
- Fixed JSON serialization of special floating-point numbers (`Infinity`, `NaN`) and `Rect.Empty` in internal test server responses.

---

## [1.0.8] - 2026-08-29

### Fixed
- Fixed auto-update download loop by replacing static version references in `UpdateManager` with dynamic assembly metadata extraction and 3-component version normalization.
- Fixed window overflow and clipped header on high-DPI scaled displays (e.g. 1080p @ 150%, 768p) by dynamically clamping initial dimensions and position to `SystemParameters.WorkArea`.
- Reduced minimum window constraints to `600px` height and `960px` width for improved compatibility with compact screens.

---

## [1.0.7] - 2026-08-29

### Fixed
- Fixed hardcoded sidebar CPU and GPU navigation button labels with dynamic silicon model detection.
- Fixed static battery health/capacity placeholder with dynamic ACPI `BatteryFullChargedCapacity` and `BatteryStaticData` calculations.

### Security
- Added cryptographic SHA256 integrity verification to auto-update binary downloader (`UpdateManager.DownloadUpdateAsync`).
- Hardened internal debug test server with origin and referer header filtering against cross-origin browser requests.

### Changed
- Isolated sensor polling loops across CPU, GPU, RAM, storage, battery, and process telemetry with independent exception boundaries to prevent single-subsystem polling freezes.
- Replaced direct configuration file writes with atomic file swap operations (`File.Move` with temporary files).

---

## [1.0.6] - 2026-08-29

### Added
- Added `Start Clocky automatically on Windows boot` toggle in Preferences (Tab 7) using unprivileged user run registry.
- Added `Start minimized to system tray on launch` toggle and `--minimized` launch argument.

### Changed
- Added staged update download deduplication to prevent redundant network downloads if an update is already staged locally.

---

## [1.0.5] - 2026-08-29

### Changed
- Replaced static battery capacity fallback with dynamic WMI `BatteryFullChargedCapacity` queries for universal laptop hardware support.

---

## [1.0.4] - 2026-08-29

### Security
- Compiled out internal automation test sockets in Release builds (`#if DEBUG`), ensuring zero listening TCP ports in production distribution.

---

## [1.0.3] - 2026-08-29

### Changed
- Relocated `battery_history.json` persistence from the executable working directory to `%LocalAppData%\Clocky\`.
- Enabled indented, human-readable JSON serialization for battery telemetry history.

---

## [1.0.2] - 2026-08-29

### Added
- Added automatic configuration migration in `AppConfig.Load()` to sanitize and redirect legacy or outdated update feed URLs.

---

## [1.0.1] - 2026-08-29

### Changed
- Simplified main application system tray icon tooltip from `Clocky — Hardware Telemetry` to `Clocky` to reduce taskbar tooltip clutter.

---

## [1.0.0] - 2026-08-29

### Initial Release

#### Architecture & Subsystems
- **WPF Rendering Engine**: Implemented Direct3D hardware-accelerated composition for dynamic 60-sample telemetry oscilloscopes with sub-pixel hover throttling.
- **Hardware Telemetry Engine**: Integrated LibreHardwareMonitorLib, WinRing0, NVML, and DXGI for ring-0 CPU/GPU temperature, clock, voltage, power, and VRAM polling.
- **CPU Topology Resolver**: Added automated core segmentation for Intel Hybrid architectures (P-cores vs. E-cores) and AMD Ryzen uniform topologies.
- **Process Resource Monitor**: Added real-time top resource consumer tracking across CPU, GPU, memory, and network subsystems.
- **Win32 System Tray Engine**: Replaced standard WinForms NotifyIcon with native Win32 `ClockyTrayIcon` wrapper using isolated HWNDs and unique `uID` assignments to prevent Windows 11 icon grouping collisions.
- **Update Subsystem**: Implemented in-app background GitHub release verification and in-place executable replacement.
- **Diagnostic Handler**: Integrated global exception interceptor and structured crash logging modal.
- **Distribution Packaging**: Configured self-contained single-file win-x64 compilation target with compression and ReadyToRun optimization.
