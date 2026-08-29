# Changelog

All notable changes to Clocky are documented in this file.

The format is based on Keep a Changelog, and this project adheres to Semantic Versioning.

## [1.1.1] - 2026-08-30

### Fixed
- Fixed auto-updater payload truncation vulnerability by enforcing atomic `.tmp` staging, stream completion checks, and minimum binary size validation.
- Added automatic transactional rollback in `apply_update.ps1` to create `.bak` executable backups and restore known-good binaries if replacement fails.

---

## [1.1.0] - 2026-08-30

### Added
- Real-time Kernel ETW network packet accounting via `Microsoft.Diagnostics.Tracing.TraceEvent` for precise per-process download and upload telemetry.
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
