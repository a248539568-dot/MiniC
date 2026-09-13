# Security Policy

[简体中文](../../SECURITY.md)

Security fixes target the latest release. Do not open a public issue for an exploitable vulnerability. Before publishing the repository, maintainers should enable GitHub Private Vulnerability Reporting and use it as the default private channel.

Reports should include the affected MiniC and Windows versions, prerequisites, impact, and a minimal reproducible sample. Never send real private Desktop files or an unredacted `%LOCALAPPDATA%\MiniC\layout.json`.

High-priority issues include unintended overwrite, movement, deletion, or disclosure of Desktop files; path traversal or Public Desktop authorization errors; command execution through Shell items or file names; privilege escalation during install or startup; and failure to restore the Explorer desktop after an abnormal exit.

Security fixes must preserve user files and layouts rather than deleting data to hide the failure.
