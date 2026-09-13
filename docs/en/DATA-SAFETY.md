# Data and Recovery

[简体中文](../DATA-SAFETY.md)

## Real Files

MiniC does not store organized files in a private directory. Real files remain in the redirected user Desktop directory or `%PUBLIC%\Desktop`. Visual ownership does not affect file pickers, uploads, or Explorer access.

## Layout

The layout is stored at:

```text
%LOCALAPPDATA%\MiniC\layout.json
```

It stores box geometry, tabs, visual ownership, and managed positions, but no file contents. Full paths can reveal private usernames and file names, so layout files must be redacted before sharing.

Writes use a temporary file followed by replacement. Before risky migration or repair, back up `%LOCALAPPDATA%\MiniC`. Backups remain sensitive.

## Legacy Migration

If the new layout does not exist, MiniC reads `%LOCALAPPDATA%\DeskNest\layout.json` and writes a safe copy to the MiniC directory while retaining the original for rollback. Much older real files under `%LOCALAPPDATA%\DeskNest\Storage` or Desktop `.DeskNest` are moved back to the Desktop without overwriting same-name items.

## Recovery

If Explorer icons do not return, exit MiniC from the notification area, wait for the watchdog, restart Windows Explorer from Task Manager, and sign out only if necessary. Never delete Desktop files or a layout merely to repair display state.

For layout recovery, exit MiniC, back up the complete MiniC data directory, restore only `layout.json` from a known-good backup, restart, and verify every box, tab, and assignment.
