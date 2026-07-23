# Changelog

## 0.3.0

- Fixed custom-titlebar maximize behavior so the main and settings windows fill the work area without a transparent white banner.
- Added optional WebRCON auto-connect, retry, reconnect status, and automatic player activity collection.
- Added a dedicated Rust settings workspace for server profile labels, Facepunch-documented browser tags, managed `serverauto.cfg` variables, and custom/default identity log paths.
- Added Carbon/Oxide detection, installed-plugin inventory, plugin-config discovery, history, and live reload commands.
- Hardened scheduled actions with per-server serialization, guarded event subscribers, manual run support, and visible last results.
- Expanded player session aggregates, persistent CPU/RAM/network/player charts, file selection contrast, and the operational action log.
- Moved lifecycle and Discord controls into a documented Automation workspace and exposed the built-in Discord status/admin bot behavior.
- Added a global default Rust installation folder picker and expanded the Info tab's Rust Operations summary.

## 0.2.0

- Added native Rust timed bans with strict Steam64 ID and duration validation.
- Added confirmation-gated bulk kick and ban actions for selected online players.
- Added persistent per-server player notes and a local moderation record workspace under `assets/data`.
- Added Carbon/Oxide whitelist permission management with clear Whitelist plugin requirements.
- Added group-ban expiry handling so timed bans are not replayed after expiration.
- Expanded smoke coverage for moderation command safety and portable record persistence.

## 0.1.0

- Created the HighPop Rust-focused Windows manager and original brand system.
- Removed inherited non-Rust game profiles and generic mod/workshop paths; HighPop now loads and operates Rust Dedicated Server only.
- Replaced the application mark, executable icon, splash screen, and project banner with Rust-server-specific branding.
- Added portable `assets/**` persistence and self-contained single-file publishing.
- Added explicit Rust public-branch SteamCMD install/update/validate settings and conflict-safe game, query, WebRCON, and Rust+ ports.
- Added Facepunch WebRCON transport, structured player parsing, moderation commands, Carbon and Oxide flows, custom-map URL support, and production presets.
- Added map/full wipe automation with mandatory safety backups.
- Hardened backup restore, preset/config/file paths, remote API tokens, stored secrets, machine tokens, server IDs, wake-on-demand listeners, and release checksum verification.
- Added Windows CI, release packaging, and dependency-free smoke tests.
