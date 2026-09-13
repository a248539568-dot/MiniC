# MiniC — Windows Desktop Organizer and Icon Grouping

[简体中文](README.md)

MiniC is a Windows 10/11 desktop organizer for desktop icon grouping, shortcut organization, and tabbed desktop boxes. Group files, folders, and shortcuts into independent boxes, merge boxes into tabs, or drag a tab out into its own box. Real files remain in the user's Desktop or Public Desktop directory.

> Current version: `0.58.9`. Supports Windows 10 / Windows 11, x64. See the license status below.

## Who It Is For

- People with crowded desktops who want to group work documents, app shortcuts, and project files.
- People who want tabbed desktop organization: merge boxes, reorder tabs horizontally, and drag vertically to detach or merge them.
- People who want to retain familiar Windows file operations, context menus, copy and paste, and existing Desktop file paths.

## Design Principles

- Organized files remain standard Desktop files and stay available to file pickers, uploads, and Explorer.
- Organizing changes visual ownership only; it does not move files into private storage.
- Each independent box or tab group has its own top-level window instead of a full-screen overlay.
- A watchdog restores Explorer's native desktop icon layer if the main process exits unexpectedly.

## Features

- Selection, marquee selection, dragging, ordering, and inline renaming on the desktop and in boxes
- Box tabs, reordering, merge and detach gestures, collapse, and list preview
- Per-box color and opacity with a simulated liquid-glass material
- Windows Shell context menus, folder drops, and Recycle Bin drops
- Blue copy badges and dimmed cut badges synchronized with the Windows file clipboard
- Incremental placement for new Desktop items without reordering existing icons
- Windows-compatible extension visibility, thumbnails, and file-drop semantics

## Technology

| Area | Technology |
| --- | --- |
| Application | C#, .NET 10, WPF |
| Windows integration | Windows Shell COM, Win32 API |
| Auxiliary UI | Windows Forms notification area icon |
| Target | Windows 10 1809+ and Windows 11, x64 |
| Distribution | Self-contained `win-x64`, Inno Setup 6 |

## Build

Use Windows 10/11 with an installed .NET 10 SDK. After cloning the source:

```powershell
dotnet restore .\MiniC.sln --configfile .\NuGet.Publish.Config
dotnet build .\MiniC.sln -c Release --no-restore
```

If this machine already has a local `.dotnet` SDK, replace `dotnet` with `.\.dotnet\dotnet.exe`. SDKs, caches, local archives, and installers are excluded from source control. See the [development guide](docs/en/DEVELOPMENT.md) for running, validation, and release instructions.

The application has no third-party NuGet package dependencies. The test project uses Microsoft.NET.Test.Sdk, xUnit, and its Visual Studio adapter. The default `NuGet.Config` clears package sources; use the explicit restore configuration above on a fresh checkout.

## Repository Layout

```text
src/MiniC/
  Controllers/   Cross-window business coordination
  Controls/      Selection, layout, and animation controls
  Models/        Persisted layout models
  Services/      File, Shell, Explorer, and system integration
  ViewModels/    Bindable UI state
  Views/         Desktop surface, box, and tray UI
installer/       Inno Setup installer
docs/            Chinese documentation
docs/en/         English documentation
```

## Data Safety

The layout is stored at `%LOCALAPPDATA%\MiniC\layout.json` and contains no file contents. It can contain private full paths and must be redacted before sharing. Existing `%LOCALAPPDATA%\DeskNest\layout.json` data is copied safely to the new MiniC location on first use.

Read [data and recovery](docs/en/DATA-SAFETY.md) before modifying persisted layouts or file operations.

## Documentation

- [Project handover (Chinese)](docs/HANDOVER.md): verified baseline, source map, and outstanding issues
- [Architecture](docs/en/ARCHITECTURE.md)
- [Development, validation, and release](docs/en/DEVELOPMENT.md)
- [Interaction contract](docs/en/INTERACTIONS.md)
- [Data and recovery](docs/en/DATA-SAFETY.md)
- [Contributing](docs/en/CONTRIBUTING.md)
- [Security](docs/en/SECURITY.md)
- [Changelog](CHANGELOG.md)

Agents and automation tools should read [AGENTS.md](AGENTS.md) before changing the repository.

## License Status

This repository is publicly visible, but the project does not yet declare an open-source license. Until the maintainers add a root `LICENSE`, the source should not be assumed to grant permission to copy, modify, or redistribute it.

Third-party attributions are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Those notices do not license MiniC itself.
