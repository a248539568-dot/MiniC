# Interaction Contract

[简体中文](../INTERACTIONS.md)

## Selection and Editing

- Double-click timing and distance use the two button presses; opening occurs on the second valid release. Interrupted presses, dragging, switching items, or clicking empty space cancel invalid click sequences.
- Metadata refresh preserves item instances, selection, and editing state. Renaming never hides the entire desktop surface; file changes during editing are deferred rather than discarded.
- Desktop and group items use the Windows double-click time and distance instead of a WPF click count that can reset when the desktop layer activates; a gesture that has entered dragging must not open the item.
- Single selection reveals the full name; deselection or multiple selection restores compact labels.
- Blank desktop and box areas support marquee selection; `Ctrl` adds or toggles items.
- Moving focus to another MiniC surface or regular application clears obsolete selection, menus, and editing state.
- `F2` and Rename start inline editing. The base name is selected by default while a visible extension remains unselected.
- The rename editor replaces only the original label below the icon. It stays within the label slot and never stretches the item horizontally or covers the icon.
- Dragging inside the rename editor selects text and must never start an icon drag.
- File-name editing commits on focus loss, including clicking elsewhere or switching windows or tabs. Enter also commits; Escape cancels. Repeated focus events submit only once, completion does not steal focus, and failed blur submissions restore the original name without changing the file.

## Dragging Files and Icons

- Arrangement, dropping, and saved-position repair use each display's working area, excluding persistent taskbars and gaps between displays. Expanded labels on the bottom row stay within the working area. Auto-hidden taskbars follow the working area returned by Windows.
- The first preview icon is centered on the pointer and the final placement matches the preview.
- Multi-selection preserves relative icon positions and moves as one block.
- Hovering ordinary icons during a drag must not replace the source. Only valid folders or the Recycle Bin show a drop target.
- Internal ordering uses the slot under the pointer and animates displaced items smoothly.
- Virtual system icons can be positioned on the desktop but cannot be organized into boxes or exported as real files.

Desktop-to-box and box-to-desktop operations change visual ownership only. External files follow Windows same-volume move, cross-volume copy, and modifier-key semantics. One drop is handled exactly once and must not create an unintended `(2)` duplicate.

## Boxes and Windows

- Non-button title-bar areas move a box; drag and double-click rename gestures must not conflict.
- Icons in icon view use a fixed top baseline, so one-line and two-line names never change the vertical position of icons in the same row.
- Boxes can collapse, switch between icon and list views, and keep independent color and opacity.
- Tabs can reorder, merge, and detach. Dragging out pulls away a full-size card with a short flexible connection that thins and breaks. Near another box the surfaces reconnect; release slides and directionally clips the card into the destination. Actual windows, text, and icons are never scaled.
- Merge feedback is a thin theme-colored outline only, with a transparent fill and unchanged title/content opacity. Previews reuse the original material, header, and content, preserving color, gradients, and alpha without a pale backing layer. Source tab groups retain their appearance; docking clips the preview without an additional whole-card fade.
- The initial dominant drag direction locks the gesture: horizontal movement only reorders tabs, even outside the box; vertical movement transfers tabs without reordering. Small or ambiguous diagonal movement stays pending. A vertical drag leaving the source tab strip starts separation, and returning to it cancels transfer.
- Both tab and whole-box merges require the pointer to hit the destination tab strip, or the title-name region of a standalone box. File content, blank body space, and header buttons do not accept merges. The hover highlight matches the accepting region.
- Drag previews apply the latest pointer position once per frame without restarting a chase animation. Their viewport stays stable while it contains the gesture, and opacity transitions occur only on state changes. Preview completion does not add another content slide; unrelated box windows survive topology changes. Unchanged layout passes preserve running reorder animations.
- A single-tab preview excludes sibling tabs. Preview and final detach placement share DIP coordinates. Returning to the source or pressing Escape retracts the card; capture loss, deactivation, and closing clean up the preview. Disabled system animations use immediate transitions. Topology changes occur only after release.
- File, system-icon, and blank desktop context menus use the public Windows Shell `IContextMenu` API. The blank menu is obtained from the Desktop view background, so commands never target a hidden Explorer icon by coordinate.
- The blank-desktop menu preserves the current system View, Sort, Refresh, New, Display settings, Personalize, and registered extension commands. The system application-extension area contains an application-icon-bearing `MiniC` submenu with Create Box and Show or Hide Boxes only; those MiniC commands return to `DesktopCoordinator` instead of changing layout state inside the menu service.
- Every registered item type created from the blank-desktop Shell New submenu, including folders, text files, shortcuts, and application extensions, uses the MiniC grid cell under the pointer that opened the menu. If occupied, placement expands to the nearest free cell before inline name confirmation begins.
- The native top-level Paste command is enabled for Shell file clipboard formats and transfers the original OLE data object to the Explorer desktop drop target.
- Copied and cut files remain selectable and use the normal selection background and outline. Both states use symmetric vector badges with the same blue color, size, and plate; only the cut file icon and name are dimmed while its badge stays clear. Desktop and box views stay synchronized and clear or update when clipboard contents change.
- The full-screen MiniC icon layer always stays below regular applications and above the Explorer desktop. An inactive box stays in the desktop layer; clicking it promotes it as a normal active foreground window without permanent Topmost, and activating another application returns it to the desktop layer. Boxes always remain above the MiniC icon layer.


- Shell notifications and FileSystemWatcher run together for desktop creates, deletes, renames, writes, sizes, and attributes. External applications do not need to emit Shell notifications.
- A background metadata comparison runs once per second and synchronizes items only when changes are found. Refresh requests are retained while busy; external renames are applied under the refresh lock. Recycle Bin counts and stock empty/full icons use a separate serialized path, avoiding cached virtual icons and late overwrites.
- Clicking a box's header, title, buttons, or tabs activates it like a content-area click, without permanent Topmost. Existing drag and title-rename gestures are preserved.
