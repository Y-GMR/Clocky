# Contributing to Clocky

## Development Prerequisites

- .NET 9.0 SDK (x64)
- Windows 10/11 x64 with Administrator privileges
- Visual Studio 2022 (v17.12+) or VS Code with C# Dev Kit

---

## Build & Test Workflow

1. Fork and clone the repository:
   ```powershell
   git clone https://github.com/YOUR_USERNAME/Clocky.git
   cd Clocky
   git checkout -b feature/your-feature-name
   ```

2. Compile and run debug build:
   ```powershell
   dotnet build src/Clocky/Clocky.csproj
   dotnet run --project src/Clocky/Clocky.csproj
   ```

3. Validate release compilation:
   ```powershell
   dotnet build src/Clocky/Clocky.csproj -c Release
   ```

---

## Technical Guidelines

- **Zero-Allocation Hot Paths**: Avoid heap allocations in per-tick sampling loops (`RenderSnapshot`, `UpdateTelemetry`). Reuse existing visual containers, bounded history buffers, and static brushes.
- **XAML Styling**: Keep UI definitions declarative in `MainWindow.xaml`. Reference dynamic theme resources (`{DynamicResource Brush...}`) for Dark and Light mode support.
- **Tray Icon Architecture**: All notification tray icons must be registered via `ClockyTrayIcon` with dedicated HWNDs and unique `uID` offsets to maintain isolation in the Windows 11 taskbar.

---

## Versioning Scheme

Clocky strictly follows Semantic Versioning (`MAJOR.MINOR.PATCH`):

- **MAJOR (`X.0.0`)**: Incompatible architectural refactors, breaking config schema changes, or core platform/driver interface rewrites.
- **MINOR (`1.X.0`)**: Backwards-compatible feature additions, new telemetry sensors, additional hardware views, or new diagnostic capabilities.
- **PATCH (`1.0.X`)**: Backwards-compatible bug fixes, performance optimizations, memory leak resolutions, and UI alignment corrections.

Git tags must use the `v` prefix matching the release version (e.g. `v1.0.0`, `v1.0.1`).

---

## Pull Request Process

1. Verify that the project builds with 0 errors and 0 warnings (`dotnet build src/Clocky/Clocky.csproj -c Release`).
2. Maintain clean, descriptive commit messages following the Conventional Commits specification.
3. Submit pull requests targeting the `master` branch.
