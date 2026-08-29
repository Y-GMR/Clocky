# Changelog

All notable changes to Clocky are documented in this file.

The format is based on Keep a Changelog, and this project adheres to Semantic Versioning.

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
