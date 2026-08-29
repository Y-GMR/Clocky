# Changelog

All notable changes to Clocky are documented in this file.

The format is based on Keep a Changelog, and this project adheres to Semantic Versioning.

## [1.0.8] - 2026-08-29

### Fixed
- Resolved auto-update loop by deriving runtime version from executing assembly metadata with 3-component normalization.
- Clamped initial window dimensions and centering to display work area, preventing viewport clipping on high-DPI scaled screens.
- Lowered minimum window constraints to 600px height by 960px width.

---

## [1.0.7] - 2026-08-29

### Fixed
- Replaced static CPU/GPU sidebar placeholders with dynamic model detection.
- Added ACPI wear calculation (`FullChargedCapacity` / `DesignedCapacity`) and bound live metrics to battery UI.

### Security
- Added cryptographic SHA256 integrity verification prior to executing update payloads.
- Added origin and referer header validation to debug test server.

### Changed
- Isolated subsystem sensor polling loops to prevent single-sensor failures from freezing UI updates.
- Converted configuration persistence to atomic file replacement.

---

## [1.0.6] - 2026-08-29

### Added
- Added Windows startup toggle via HKCU Run registry in Preferences.
- Added start-minimized option and `--minimized` command-line argument.

### Changed
- Added update download deduplication to skip redundant network transfers.

---

## [1.0.5] - 2026-08-29

### Changed
- Added WMI fallback queries for battery sensor initialization on supported laptop architectures.

---

## [1.0.4] - 2026-08-29

### Security
- Excluded test server sockets from Release builds via `#if DEBUG`.

---

## [1.0.3] - 2026-08-29

### Changed
- Moved `battery_history.json` to `%LocalAppData%\Clocky\`.
- Enabled indented JSON formatting for battery history exports.

---

## [1.0.2] - 2026-08-29

### Fixed
- Added configuration sanitizer to migrate legacy update feed URLs automatically.

---

## [1.0.1] - 2026-08-29

### Changed
- Simplified system tray tooltip to `Clocky`.

---

## [1.0.0] - 2026-08-29

### Initial Release

#### Architecture & Subsystems
- **WPF Rendering Engine**: Direct3D hardware-accelerated composition for rolling 60-sample telemetry oscilloscopes.
- **Hardware Telemetry Engine**: Ring-0 CPU/GPU temperature, clock, voltage, power, and VRAM polling via LibreHardwareMonitorLib, NVML, and DXGI.
- **CPU Topology Resolver**: Core segmentation for Intel Hybrid architectures (P-cores / E-cores) and AMD Ryzen topologies.
- **Process Resource Monitor**: Real-time tracking of top resource consumers across CPU, GPU, memory, and network.
- **Win32 System Tray Engine**: Custom `ClockyTrayIcon` wrapper using isolated HWNDs to prevent Windows 11 icon grouping collisions.
- **Update Subsystem**: In-app GitHub release verification and in-place executable replacement.
- **Diagnostic Handler**: Global exception interceptor and structured crash logging modal.
- **Distribution Packaging**: Self-contained single-file win-x64 compilation with ReadyToRun optimization.
