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
- The rename editor replaces only the original label below the icon. It scrolls horizontally on one line within the label slot and never stretches the item or covers the icon.
- Ordinary files expose their complete names while renaming, including editable/removable extensions, independently of Explorer's static-label setting. The `.lnk` and `.url` shortcut suffixes remain hidden and preserved. Only an ordinary file's stem is selected initially; folder names and editable shortcut names are selected in full.
- Background host reconciliation preserves title/file rename drafts and selection. Reconfiguring the displayed tab is not a user tab switch and must not submit or close an editor.
- Dragging inside the rename editor selects text and must never start an icon drag.
- File-name editing commits on focus loss, including clicking elsewhere or switching windows or tabs. Enter also commits; Escape cancels. Repeated focus events submit only once, completion does not steal focus, and failed blur submissions restore the original name without changing the file.

## Dragging Files and Icons

- While dragging inside a box, original tiles retain their layout slots but do not draw icons, names, or selection backgrounds. The floating preview is the only visible copy. Completion, cancellation, and exceptions restore sources in both icon and list views, including multiple selection.
- Arrangement, dropping, and saved-position repair use each display's working area, excluding persistent taskbars and gaps between displays. Expanded labels on the bottom row stay within the working area. Auto-hidden taskbars follow the working area returned by Windows.
- The first preview icon is centered on the pointer and the final placement matches the preview.
- Multi-selection preserves relative icon positions and moves as one block.
- Hovering ordinary icons during a drag must not replace the source. Only valid folders or the Recycle Bin show a drop target.
- Internal ordering uses the slot under the pointer and animates displaced items smoothly.
- Virtual system icons can be positioned on the desktop but cannot be organized into boxes or exported as real files.

Desktop-to-box and box-to-desktop operations change visual ownership only. External files follow Windows same-volume move, cross-volume copy, and modifier-key semantics. One drop is handled exactly once and must not create an unintended `(2)` duplicate.

## Boxes and Windows

- The window activates on the first press, including content margins, without consuming it. Deferred cleanup and focus restoration are guarded by the interaction version. Header buttons handle presses separately from header dragging; pointer activation does not queue a focus move to the selection surface. Context-menu state is set before opening so deactivation cannot prematurely clear it.
- Titles and item names use clear Display/Grayscale text with the shared soft drop shadow. All three materials use an 8-DIP corner radius: native Frosted uses DWMWCP_ROUND and the WPF materials share GroupGeometry. Window regions do not visually clip Acrylic.
- Theme colors use a shared, more distinguishable palette and stronger Crystal tint. Changing color preserves material, transparency, and layout; color differences naturally diminish near full transparency.
- Detached preview layers use explicit layout sampling bounds with offsets applied once. Whole-box drag hands off the source immediately to avoid overlapping headers. WPF cards, shader geometry, and scaled preview shadows share the shared 8-DIP corner radius.
- Crystal keeps its shader surface size and preview host stable, scaling with RenderTransform while sampling transformed screen coordinates. Acrylic submits one SetWindowPos per rendered frame. All modes share one render clock and stop requesting frames after motion settles.
- Background transparency is consistently 0–100% in the box menu and tray: higher values mean less tint. Acrylic still blurs the backdrop. Layout files retain their existing Opacity meaning; the UI displays its complement. The native minimum tint alpha is 1/255 for compatibility, and text/icon opacity is unchanged.
- New boxes and widgets share free-space placement within the source or an existing visible host's monitor work area, keeping a 12-DIP gap when space permits. Without a visible host, placement uses the primary work area. A full screen falls back to the least overlapping location.
- Merge highlighting uses an overlay that cannot change destination or tab layout. Hit testing uses physical screen coordinates from the unscaled layout.
- Materials are limited to Crystal, Frosted, and Solid. Crystal refracts desktop wallpaper near rounded edges, with subtle dispersion and rim light while preserving the center. It does not refract other apps, desktop icons, or live wallpapers. Solid retains the former Clear appearance. Legacy Clear/Satin/Gradient and unknown values normalize to Solid; Crystal/Frosted remain supported.
- Crystal uses per-monitor wallpaper images and placement from IDesktopWallpaper, decoded and cached in the background. Moving changes sampling coordinates; display/settings changes refresh the cache. Drag previews separate the wallpaper shader from their foreground snapshot, keeping content sharp and avoiding a frozen wallpaper patch.
- Frosted uses a contained rounded surface, texture, and adjustable blur layer by default; it does not sample the desktop wallpaper. The explicit see-through option enables window-level backdrop blur. Text and icons remain sharp, and switching material or tabs clears legacy Native Acrylic state without changing saved layout.
- Native glass previews use a card-sized HWND, since the system effect does not obey WPF's internal clipping. Hidden/returned previews disable blur; closing clears native resources. Ordinary previews keep the stable larger canvas. No screen capture is used for the product backdrop.
- Detaching starts with a small preview positioned at the pointer before its window is shown. It grows with distance from the source tab strip and shrinks when returning. Both source and destination strips participate in proximity scaling; position and scale anchor update together on each rendered frame.
- Non-button title-bar areas move a box; drag and double-click rename gestures must not conflict.
- Icons in icon view use a fixed top baseline, so one-line and two-line names never change the vertical position of icons in the same row.
- Boxes can collapse, switch between icon and list views, and keep independent color and opacity.
- White box titles, filenames, widget text, and icons use the same soft dark drop shadow, so they remain readable over white desktop pixels and pale materials without forming a tight outline, changing the foreground color, or entering backdrop sampling.
- Any source box can merge into any chosen destination independently of creation or host-list order. Whole-box merging appends all source tabs, activates the dragged tab, and preserves destination identity, position, size, collapse state, and appearance. Dragging one tab transfers only that tab. Once armed, the source scales to about 38%; leaving restores full size without changing saved dimensions.
- Icon reordering uses stable layout slots rather than animated visual bounds, with hysteresis near slot boundaries. Interrupted movement continues from the current visible position even after layout invalidation, avoiding repeated back-and-forth displacement.
- Armed merge feedback is a borderless release-to-merge instruction. Previews reuse the original material, header, and content without a pale backing layer.
- The initial dominant drag direction locks the gesture: horizontal movement only reorders tabs, even outside the box; vertical movement transfers tabs without reordering. Small or ambiguous diagonal movement stays pending. A vertical drag leaving the source tab strip starts separation, and returning to it cancels transfer.
- Tab merges use the central 60% of destination width and height; whole-box window merges use the complete destination bounds. Native window Z order chooses overlapping targets, skipping the entire source host. The edge of a nearer box blocks targets hidden beneath it.
- A stable 0.2-second hover arms a whole-box destination; tab drags keep the central-zone rule. Moving more than 10 DIP before arming or changing targets restarts the timer. An initial overlap must be left and entered again. Release must still hit the same already-highlighted destination; releasing alone cannot arm a merge.
- Whole-box movement and backdrop alignment are coalesced to the render frame, and entering or leaving a destination reuses one scale transform instead of restarting competing animations.
- Drag previews apply the latest pointer position once per frame without restarting a chase animation. Their viewport stays stable while it contains the gesture, and opacity transitions occur only on state changes. Preview completion does not add another content slide; unrelated box windows survive topology changes. Unchanged layout passes preserve running reorder animations.
- A single-tab preview excludes sibling tabs; preview and final detach placement share DIP coordinates. Whole-box release or capture loss preserves the moved position, including after passing through a merge target. Escape explicitly cancels a whole-box move and restores its origin. Returning a tab to its source cancels transfer. Completion, cancellation, deactivation, and closing clear previews and the merge timer. Topology changes occur only after release.
- File, system-icon, and blank desktop context menus use the public Windows Shell `IContextMenu` API. The blank menu is obtained from the Desktop view background, so commands never target a hidden Explorer icon by coordinate.
- The blank-desktop menu keeps Shell-backed New, Display settings, Personalize, and registered extension commands, while MiniC generates and persists View, Sort, Group, Auto arrange, and Align to grid against its own icon layer. Explorer's Refresh command is removed because it would refresh the hidden `SysListView32`; MiniC inserts a refresh command that refreshes its own layer. The system application-extension area contains an application-icon-bearing `MiniC` submenu with Create Box and Show or Hide Boxes only; those MiniC commands return to `DesktopCoordinator` instead of changing layout state inside the menu service.
- A box blank-area menu keeps Shell file actions and MiniC refresh, but does not expose commands that would change desktop ordering. New and Paste items created from that menu remain in the real Desktop folder and are assigned to the current box's visual membership after the Shell operation settles.
- Windows 11's modern menu is hosted internally by Explorer and has no public third-party invocation API; MiniC uses the public `IContextMenu` surface and records menu construction, query, and invocation failures in its local diagnostic log.
- Every registered item type created from the blank-desktop Shell New submenu, including folders, text files, shortcuts, and application extensions, uses the MiniC grid cell under the pointer that opened the menu. If occupied, placement expands to the nearest free cell before inline name confirmation begins.
- The native top-level Paste command is enabled for Shell file clipboard formats and transfers the original OLE data object to the Explorer desktop drop target.
- Copied and cut files remain selectable and use the normal selection background and outline. Both states use symmetric vector badges with the same blue color, size, and plate; only the cut file icon and name are dimmed while its badge stays clear. Desktop and box views stay synchronized and clear or update when clipboard contents change.
- The full-screen MiniC icon layer always stays below regular applications and above the Explorer desktop. An inactive box stays in the desktop layer; clicking it promotes it as a normal active foreground window without permanent Topmost, and activating another application returns it to the desktop layer. Boxes always remain above the MiniC icon layer.


- Shell notifications and FileSystemWatcher run together for desktop creates, deletes, renames, writes, sizes, and attributes. External applications do not need to emit Shell notifications.
- A background metadata comparison runs once per second and synchronizes items only when changes are found. Refresh requests are retained while busy; external renames are applied under the refresh lock. Recycle Bin counts and stock empty/full icons use a separate serialized path, avoiding cached virtual icons and late overwrites.
- Clicking a box's header, title, buttons, or tabs activates it like a content-area click, without permanent Topmost. Existing drag and title-rename gestures are preserved.
