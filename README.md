# Clocky

Hardware telemetry and observability platform for Windows 10 and 11 (x64), built with .NET 9 and WPF.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform: Windows x64](https://img.shields.io/badge/Platform-Windows%20x64-0078D6)](https://github.com/Y-GMR/Clocky)
[![Framework: .NET 9.0](https://img.shields.io/badge/Framework-.NET%209.0%20WPF-512BD4)](https://dotnet.microsoft.com/)

---

## 1. System Architecture & Features

Clocky ingests ring-0 kernel telemetry, DirectX performance counters, ETW network traces, and OS process metrics, rendering real-time graphs and system tray badges.

### Core Subsystems
- **CPU Topology Engine**: Detects Intel Hybrid architectures (separating Performance Cores and Efficiency Cores) and AMD Ryzen uniform CCX/CCD topologies. Tracks per-core clocks, VID, and load.
- **Hardware Telemetry Pipeline**: Queries CPU/GPU sensors via LibreHardwareMonitorLib (`WinRing0`), NVML, and DXGI at configurable polling intervals (default 1000ms).
- **Process Resource Attribution**: Tracks instantaneous top resource consumers across CPU, GPU (3D and VRAM engines), system memory, and network throughput.
- **Real-Time Oscilloscopes**: Pre-allocated fixed-capacity ring buffers rendering rolling 60-sample waveforms with cached max-bound calculations and sub-pixel hover interpolation.
- **Win32 Notification Tray Engine**: Custom `ClockyTrayIcon` implementation wrapping `Shell_NotifyIcon` with independent HWNDs and unique `uID` assignments, preventing Windows 11 taskbar icon grouping collisions.
- **Diagnostics & Error Interception**: Global exception interceptor with structured crash logging and diagnostic report modals.

---

## 2. Requirements

- **Operating System**: Windows 10 (64-bit, Version 1903+) or Windows 11 (64-bit).
- **Permissions**: Administrator privileges (required for ring-0 MSR and EC sensor access).
- **Dependencies**: None for standalone distribution (`dist/Clocky.exe` is self-contained).

---

## 3. Installation & Distribution

### Standalone Executable
Download `Clocky.exe` from the latest release and run as Administrator. No external runtime installation is required.

### InnoSetup Installer
An optional installer configuration is provided at `scripts/Clocky_Setup.iss` for automated deployment and start menu integration.

### Binary Verification (SHA256)
Official release builds include a `SHA256SUMS.txt` checksum manifest. To verify the integrity of downloaded binaries in PowerShell:
```powershell
Get-FileHash -Algorithm SHA256 .\Clocky.exe
```

---

## 4. Building from Source

### Prerequisites
- .NET 9.0 SDK (x64)
- PowerShell 7+ or Windows PowerShell 5.1

### Build Commands
```powershell
# Restore and compile debug build
dotnet build src/Clocky/Clocky.csproj

# Publish self-contained single-file release executable
powershell.exe -ExecutionPolicy Bypass -File scripts/publish_standalone.ps1
```
The compiled output is placed at `dist/Clocky.exe`.

---

## 5. Configuration File Format

Configuration is stored at `%APPDATA%\Clocky\config.json`.

```json
{
  "PollingIntervalMs": 1000,
  "AlwaysOnTop": true,
  "CloseToTray": true,
  "MinimizeToTray": false,
  "IsDarkTheme": true,
  "AutoCheckUpdates": true,
  "EnableDebugLog": false,
  "TraySensors": [
    {
      "Id": "cpu.temp",
      "Label": "CPU Package Temp (°C)",
      "Enabled": true,
      "Order": 1,
      "BackgroundColorHex": "",
      "TextColorHex": ""
    }
  ]
}
```

---

## 6. Technical Documentation

- [ARCHITECTURE.md](docs/ARCHITECTURE.md): Detailed hardware engine architecture and WPF rendering pipeline.

---

## 7. Development & Transparency

For full transparency:
- **Codebase Implementation**: The software codebase (C#, XAML, build automation, CI/CD workflows, and documentation) was authored programmatically with AI agentic coding assistance.
- **Human Oversight & Assets**: Architectural direction, feature requirements, UI/UX evaluation, empirical hardware testing, and design assets (icons, branding, and imagery) were directed and provided by the human maintainer.

---

## 8. License

Clocky is licensed under the [MIT License](LICENSE).
