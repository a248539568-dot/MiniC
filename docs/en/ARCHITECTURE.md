# MiniC Architecture

[简体中文](../ARCHITECTURE.md)

## Goal

MiniC combines real Desktop files, a custom-rendered desktop surface, and independent box windows. It provides visual organization without breaking normal Windows access to Desktop files.

## Invariants

1. Real files remain in the user's Desktop or Public Desktop directory. Organizing changes visual ownership only.
2. An item appears either on the unorganized desktop surface or in exactly one box, never both and never neither.
3. `DesktopCoordinator` is the only coordinator for windows, ownership, persistence, and refresh operations.
4. One independent box owns one `DeskGroupWindow`; boxes sharing a `TabGroupId` share one host window.
5. Explorer's native icon layer is hidden only after MiniC is ready and is restored on normal or abnormal exit.
6. Visual state is committed only after file operations succeed. Topology changes must be validated and reversible.

## Components

```text
App
 └─ DesktopCoordinator
     ├─ DesktopSurfaceWindow (1)
     ├─ DeskGroupWindow (0..n)
     ├─ ShellMenuOwnerWindow
     ├─ DesktopFileService / LayoutStore
     ├─ NativeDesktopService / ShellContextMenuService
     └─ DesktopLayerHostService
```

`App` owns startup, single-instance behavior, diagnostics, and cleanup. `DesktopCoordinator` owns cross-window business state. Views submit actions through callbacks, services isolate Windows and file-system capabilities, and ViewModels do not perform file operations.

Shell notifications and file-system watchers run together. `DesktopDirectorySnapshot` provides background metadata reconciliation; refresh requests remain pending while busy, and external renames are applied under the refresh lock. `RecycleBinIconService` alone queries and assigns empty/full icons, preventing generic cached icons from overwriting current state.

## Window Layering

MiniC uses independent top-level tool windows and does not attach windows to WorkerW with cross-process `SetParent`. The intended Z order is:

```text
Regular applications
MiniC box windows
MiniC desktop surface
Explorer desktop
```

MiniC windows do not appear in the taskbar or `Alt+Tab`. A user click on a box's content, header, buttons, or tabs activates it as a normal foreground window without permanent Topmost; an inactive box returns to the desktop layer. The full-screen desktop surface always remains below regular applications.

## Ownership and Topology

`LayoutState.NativeDesktop.Assignments` maps full paths to box IDs. `ManagedPositions` stores positions managed by MiniC. Organizing adds an assignment; dragging back to the desktop removes it.

`GroupState.Id` identifies a box, `TabGroupId` identifies a shared host, and `TabOrder` stores tab order. Merge and detach operations snapshot state, update topology, validate box IDs and assignment counts, rebuild affected hosts once, and roll back on failure.

## Startup and Persistence

MiniC waits for Explorer and the first DWM composition before taking over the desktop. The watchdog restores Explorer icons after a crash or forced termination.

New layouts are stored at `%LOCALAPPDATA%\MiniC\layout.json`. If only `%LOCALAPPDATA%\DeskNest\layout.json` exists, `LayoutStore` loads it and writes a safe copy to the new path. Older private-storage migration remains isolated in `LegacyStorageMigrationService`.
