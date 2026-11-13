# Development Guide

[简体中文](../DEVELOPMENT.md)

## Requirements

- Windows 11 Build 22000+
- .NET 10 SDK
- An editor compatible with the selected .NET 10 SDK; repository-local CLI builds have been verified
- Inno Setup 6 only when creating an installer

MiniC targets `net10.0-windows10.0.26100.0`, WPF, and a small Windows Forms tray integration. Live crystal uses shared DXGI/D3D11 GPU textures and Composition swap chains without per-frame CPU background readback. Meaningful runtime validation requires a Windows desktop session.

## Build and Run

The source repository excludes `.dotnet`, `.nuget`, `.local`, and `dist`. On a fresh clone, install the .NET 10 SDK and use `dotnet` in place of the local SDK path below. Existing local installations may continue to use `.dotnet/dotnet.exe`.

```powershell
.\.dotnet\dotnet.exe restore .\MiniC.sln --configfile .\NuGet.Publish.Config
.\.dotnet\dotnet.exe build .\MiniC.sln -c Release --no-restore
& .\src\MiniC\bin\Release\net10.0-windows10.0.26100.0\MiniC.exe
```

Close other MiniC instances before debugging and verify that Explorer's native icons return after the debugger exits.

The application uses Microsoft.Data.Sqlite 10.0.12, lunar-csharp 1.6.8 and Vortice.Direct3D11 3.8.3 with its DXGI/SharpGen dependencies. The test project requires Microsoft.NET.Test.Sdk 17.14.1, xunit 2.9.3, and xunit.runner.visualstudio 3.1.4. The default NuGet.Config clears package sources; the explicit restore configuration enables nuget.org. Use `--no-restore` only after restoring successfully. Run `tools/Compile-CrystalShader.ps1` after editing shaders; Windows SDK fxc is required. Replace the local SDK path with `dotnet` when using a system SDK.

## Main Entry Points

| Concern | Path |
| --- | --- |
| Startup and diagnostics | `src/MiniC/App.xaml.cs` |
| Business coordination | `src/MiniC/Controllers/DesktopCoordinator.cs` |
| Layout model and storage | `src/MiniC/Models/LayoutState.cs`, `Services/LayoutStore.cs` |
| File operations | `src/MiniC/Services/DesktopFileService.cs` |
| Explorer integration | `src/MiniC/Services/NativeDesktopService.cs` |
| Shell menus | `src/MiniC/Services/ShellContextMenuService.cs` |
| Desktop and box windows | `src/MiniC/Views/` |
| Crash fallback | `src/MiniC/Services/DesktopFallbackWatchdog.cs` |

Views must not bypass `DesktopCoordinator` for cross-window state, and ViewModels must not perform real file operations.

## Naming

- Use `MiniC` for the product, assembly, solution, project, root namespace, and release artifacts.
- Do not introduce `MiniC桌面`, `MiniCDesktop`, or `DeskNest` as current product names.
- Domain names such as `DeskGroup` and `DesktopSurfaceWindow` describe desktop responsibilities and do not need brand-driven renaming.
- Legacy names may remain only in clearly documented data migration, installer upgrade, single-instance, and old-process cleanup paths.

## Validation

`.github/workflows/build.yml` restores dependencies, builds Release, and runs independent tests on GitHub's Windows runner. It does not start the application in normal desktop takeover mode or replace interactive installation and desktop validation.

Build and run the independent unit tests first:

```powershell
.\.dotnet\dotnet.exe build .\MiniC.sln -c Release --no-restore
.\.dotnet\dotnet.exe test .\tests\MiniC.Tests\MiniC.Tests.csproj -c Release --no-build --no-restore
```

The September 10, 2026 baseline includes a fresh restore using an empty package cache, a clean Release build, five passing unit tests, and nine passing isolated smoke tests. See the [handover record (Chinese)](../HANDOVER.md) for limitations and outstanding issues.

Do not run the former application smoke-test arguments. Daily verification uses Release builds and unit tests that never show a window or take over Explorer. Validate real desktop interactions manually in a separate test session, never automatically on an occupied desktop.

## Release

Keep the version synchronized in `src/MiniC/MiniC.csproj` and `installer/MiniC.iss`. Publish with:

```powershell
.\.dotnet\dotnet.exe restore .\src\MiniC\MiniC.csproj `
  -r win-x64 --configfile .\NuGet.Publish.Config
.\.dotnet\dotnet.exe publish .\src\MiniC\MiniC.csproj `
  -c Release -r win-x64 --self-contained true --no-restore `
  -o .\dist\MiniC-v<version>-win-x64
```

Build the installer with `installer/MiniC.iss`. Before release, validate clean install, upgrade, uninstall, crash fallback, and layout preservation; include `THIRD_PARTY_NOTICES.md`, publish a SHA-256 hash, and use code signing for formal distribution.
