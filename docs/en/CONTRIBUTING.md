# Contributing

[简体中文](../../CONTRIBUTING.md)

MiniC integrates directly with the Windows desktop and real file operations. Contributions must prioritize data safety, Windows-consistent behavior, and recovery after failure.

Before changing code, read the [architecture invariants](ARCHITECTURE.md) and the relevant interaction or data contract. Keep each change focused, route cross-window behavior through `DesktopCoordinator`, keep file operations out of ViewModels, and preserve compatibility with older layout JSON.

At minimum, run a Release build and the relevant smoke tests. Desktop layering, Shell, drag-and-drop, file-system, and multi-monitor changes also require real Windows validation. Pull requests should explain the cause, behavioral change, affected data boundaries, validation performed, and remaining risk.

Do not commit `bin`, `obj`, `dist`, local layouts, private Desktop paths, usernames, or file names. User-visible changes belong in `CHANGELOG.md`, and implementation work must be recorded at the top of `development.log`.

MiniC does not yet declare a project license. A root `LICENSE` and contribution-licensing policy are required before accepting external contributions.
