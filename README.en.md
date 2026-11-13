# MiniC — Windows Desktop Organizer and Icon Grouping

[简体中文](README.md)

MiniC is a Windows 11 desktop organizer for desktop icon grouping, shortcut organization, and tabbed desktop boxes. Group files, folders, and shortcuts into independent boxes, merge boxes into tabs, or drag a tab out into its own box. Real files remain in the user's Desktop or Public Desktop directory.

The Chinese UI also includes a compact Planner widget. One 340x380 host contains Overview, Focus, Calendar and Tasks pages; tasks and scheduled events share one SQLite entry set and the calendar aggregates them without creating extra windows. The weather widget keeps the same small host size and uses compact current, hourly and multi-day sections.

> Current local version: `0.60.82`. Targets Windows 11, x64. See the license status below.

Version 0.60.82 rewrites whole-box merge drag-out animation: the scale is anchored at the mouse grab point and driven by one render-frame state machine, so entering or leaving a destination cannot restart competing timelines or make the window wobble. Pointer input remains frame-coalesced, and the merge instruction stays borderless.

Version 0.60.80 adjusts whole-box merging: any source can merge into the complete bounds of the chosen destination regardless of creation or list order. Overlapping targets follow native window order. Pause inside the destination for 0.2 seconds and release after the merge animation appears; an initial overlap must be left and entered again. Passing through a target or losing capture keeps the moved position, while Escape restores the origin. New boxes and widgets prefer nearby free space within the current monitor's work area, minimizing overlap when it is full.

Version 0.60.77 preserves box-title and file-name rename drafts during background host reconciliation. Ordinary desktop and box files always expose the complete filename for editing, including changing or removing the extension; only the stem is selected initially. The `.lnk` and `.url` shortcut extensions stay hidden and preserved. Editors scroll horizontally within the fixed grid, and renamed files have their icons refreshed.

The blank-desktop menu uses the public Windows Shell `IContextMenu` surface for file operations, while MiniC owns View, Sort, Group, Auto arrange, Align to grid, and Refresh for its custom icon layer. A box blank-area menu supports Shell New and Paste and assigns created items to that box after the operation completes. Windows 11's modern Explorer menu is an internal host without a public third-party invocation API.

## Download

[Windows x64 installer](https://github.com/a248539568-dot/MiniC/releases/download/v0.59.0/MiniC-Setup-v0.59.0.exe) · [Release notes and SHA-256 checksums](https://github.com/a248539568-dot/MiniC/releases/latest)

The installer includes the .NET runtime. Current local build: 0.60.82; the public download remains 0.59.0. Local installer: `dist/MiniC-Setup-v0.60.82.exe` (57,865,224 bytes, SHA-256 `DD6301BDAC39406818817FE92F0240D0FA691570A8737972F929B229BA27FB97`), with an adjacent SHA-256 file. The self-contained publish contains 488 files and the Release suite passes 334/334 tests, including grab-point anchoring, the single scale state machine, frame-coalesced whole-box movement, borderless merge feedback, whole-box full-bounds hit testing, tab center-zone preservation, 0.2-second dwell, and release gating, plus the earlier source/destination/order combinations, overlap protection, native window order, release/capture-loss placement, Escape cancellation, and new-box placement. Actual desktop dragging and appearance still require manual acceptance; no visible test boxes or mouse automation were used.

**Capture settings:** “Hide from capture” applies to all three materials, both sources and detach previews. The window owns foreground affinity; native hosts manage only their own affinity. Disabling hiding preserves refraction by changing the background input. Window capture requests borderless access once; Windows keeps its capture indicator if access is not granted. Unavailable or incorrectly sized rear-window pixels remain transparent. Weather IP location, geocoding and providers explicitly bypass HTTP proxies; OS-level VPN/TUN routing remains controlled by Windows. Previously cached proxy-exit cities can be reset with the IP relocation menu. Drag frames retain coalesced input, paired window placement and one outstanding frame. No app launch or mouse automation was used; real capture appearance and dragging await manual acceptance.

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
| Target | Windows 11 Build 22000+, x64 |
| Distribution | Self-contained `win-x64`, Inno Setup 6 |

## Build

Use Windows 11 with an installed .NET 10 SDK. After cloning the source:

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
