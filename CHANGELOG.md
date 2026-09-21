# Changelog

## 0.8.2

- Expanded new-server creation so Game, Query, WebRCON, and Rust+ ports are editable, checked against saved profiles and active Windows listeners, and can be reassigned together.
- Added the same conflict validation to existing profiles, blocked persistence of invalid legacy collisions, prevented shared installation directories, and assigned verified free ports to clones.
- Reworked console ingestion with bounded UI batches, source and severity parsing, ANSI cleanup, pause/resume, export, recycling virtualization, and bounded duplicate tracking.
- Restored server reporting after process reattachment by tailing the active Rust log with truncation and rotation handling.
- Made firewall and UPnP port setup transactional, including rollback and visible failure reporting; UPnP now includes WebRCON.
- Avoided unchanged encrypted profile rewrites during periodic autosave and captured UI-owned profile state safely.
- Added a shared portable-package builder and CI validation. The ZIP now extracts to `HPRM/HighPop.exe` and `HPRM/assets/**`, while the standalone executable remains available.
- Expanded the multi-server smoke matrix for invalid, conflicting, and independently allocated port sets.

## 0.8.1

- Added a deterministic lifecycle coordinator with separate persisted desired state and observed phase, monotonic generations, operation IDs, initiators, reasons, and transition timestamps.
- Made explicit Stop persist stopped intent before process shutdown and invalidate older queued Start, restart, update, wipe, health, scheduler, Discord, REST, wake-on-demand, and log-rule work.
- Preserved explicit running intent across manager restarts while keeping legacy profiles stopped unless Auto-start is independently enabled.
- Routed Auto-start, Always-on recovery, scheduler, health checks, log rules, wake-on-demand, Discord, REST, update workflows, and UI controls through lifecycle-labelled operations.
- Added visible lifecycle detail and distinct Starting, process-running, Rust-ready, WebRCON-ready, Recovering, Maintenance, Degraded, and Faulted phases.
- Added smoke coverage for legacy migration, verified reattachment, persisted-running recovery, manual-stop precedence, and stale generation rejection.
- Added lifecycle deadlines, runtime cancellation for superseded starts, and a bounded 32-entry durable operation-result journal.
- Separated process, Rust-ready, WebRCON-ready, and player-sample health timestamps and exposed them in a local Support health summary.
- Replaced fixed WebRCON retry polling with bounded exponential reconnect delays and jitter, cancelled when newer lifecycle intent wins.
- Added a redacted ZIP support bundle containing an explicit-safe profile summary, lifecycle history, health signals, environment details, and bounded recent logs.

## 0.8.0

- Fixed duplicate Rust custom-variable rows: reload now takes the latest active `server.cfg` assignment case-insensitively, and save retains only one active assignment while preserving older duplicates as audit comments.
- Reworked close/reopen continuity: close-to-tray retains full management, explicit manager exit detaches without stopping Rust, and reattachment verifies PID, start time, and executable path before restoring monitoring.
- Added WebRCON fallback for commands sent to a reattached Rust process, whose original redirected console cannot be recovered by Windows.
- Replaced the legacy Windows Run entry with a per-user Task Scheduler logon task that launches HighPop directly in background mode.
- Restyled the close/exit dialog with the shared HighPop brand palette and clear background-versus-exit behavior.
- Established `main` as the production branch and `testing` as the integration/release-candidate branch, with CI running on both.
- Reworked Stop into an explicit `server.save` → `quit` sequence with a configurable 15–600 second timeout, and reworked Force Stop to save, request immediate exit, then enforce process termination after five seconds.
- Locked direct Install/Update while Rust is running and expanded safe live-update monitoring with configurable in-game countdown broadcasts before the save/stop/update/restart workflow.
- Applied the graphite, violet, and cyan Rogue ecosystem identity to the shared application palette, headers, cards, navigation, selections, and lifecycle command deck.
- Corrected lifecycle policy so `Always-on` protects only a server HighPop deliberately started or reattached; opening the manager no longer starts a stopped always-on profile unless `Auto-start` is separately enabled.
- Added smoke coverage for the startup-policy boundary to prevent `KeepOnline` and `AutoStart` from becoming coupled again.
- Extended the verified RogueRust installer to support both frameworks: Oxide/uMod installs to `RustDedicated_Data/Managed`, while Carbon installs to `carbon/extensions`.
- Made dual-framework updates transactional with automatic full-operation rollback, exact per-target backup copies, bounded retention, stricter Carbon detection, and failure-path smoke coverage.
- Replaced the legacy HighPop artwork with a cleaner Rogue ecosystem-aligned icon, banner, in-app wordmark, and splash system.
- Carried forward the v0.7 custom-variable workspace and visual refresh as the foundation of the v0.8 automation and portability milestone.

## 0.7.0

- Rebuilt Rust custom variables as an observable workspace so add/remove/edit operations update immediately instead of relying on a full view refresh.
- Added variable filtering, pending-change counts, automatic timestamped `server.cfg` rollback copies, and a Save + apply-live action for running Rust servers.
- Added first-class RogueRust extension installation/update from the latest public GitHub release, including SHA-256 verification and rollback copies of replaced DLLs.
- Added RogueRust installed-version detection and one-click `roguerust.version` / `roguerust.readiness` diagnostics.
- Refined cards, spacing, status pills, hierarchy, and the Mods workspace while retaining HighPop's lightweight native WPF design.
- Updated the roadmap after comparing HighPop with AMP, GameServerApp, LinuxGSM, and Pterodactyl workflows.

## 0.6.0

- Added an always-on production policy, enabled by default, that resumes Rust with HighPop and never shuts it down merely because the player count is zero.
- Kept always-on Rust processes running when the desktop manager closes or updates, then reattached to their persisted process IDs on the next launch.
- Made unexpected-exit recovery persistent across failed starts, with server-level crash history and exponential backoff capped at five minutes to avoid resource-heavy crash loops.
- Kept planned manual and scheduled stops authoritative while handing failed restart, update, and wipe starts back to always-on recovery.
- Hardened scheduled task execution so pre-action lookup/gate failures cannot leave tasks stuck as running or leak per-server locks.
- Added scheduler health timestamps, failure counts, execution duration, corrupt-file quarantine, and repair of missing recurring next-run values.
- Added usable one-time schedules for the next occurrence of a selected time.
- Updated templates, cloned server profiles, smoke coverage, roadmap, and release metadata for HighPop v0.6.0.

## 0.5.0

- Prevented slow Rust startup from being treated as a WebRCON freeze or an empty server by adding readiness detection, configurable startup grace, and fresh-player-sample requirements.
- Extended automatic WebRCON startup handling from a fixed one-minute loop to configurable first-attempt and retry windows (60 seconds and 15 minutes by default).
- Made periodic Rust update checks non-destructive: HighPop now compares installed and current Steam branch build IDs before stopping a live server.
- Added process exit codes, uptime, requested stop reasons, hard RAM-cap warnings, and protection against stale exit callbacks removing a replacement process.
- Extended graceful Rust shutdown from 5 to 30 seconds before HighPop force-closes the process.
- Corrected custom-window maximize sizing to use the Windows monitor work area so the manager and settings window no longer extend behind the taskbar.
- Applied the Files-page orange/brown selection palette to dropdowns throughout the manager.
- Began roadmap Stage 5 with an opt-in, versioned local Rust telemetry stream and per-server age/storage retention controls.

## 0.4.0

- Made the identity's `cfg/server.cfg` authoritative for Rust custom variables and added explicit reload, save, and open-file controls.
- Added safe parsing of existing active `server.cfg` assignments so HighPop displays operator-defined variables instead of only its starter rows.
- Preserved comments and unrelated settings while updating changed rows, disabling removed assignments, and adding new enabled variables in a managed block.
- Added one-time migration of HighPop's v0.3 managed block from `serverauto.cfg`; existing `server.cfg` values win and unrelated `serverauto.cfg` content is retained.
- Added regression coverage for config loading, precedence, migration, preservation, disabling, input validation, and idempotent writes.
- Added a direct self-contained Windows executable, ZIP package, per-file SHA-256 checksums, and a machine-readable release manifest to the release workflow.
- Added optional Authenticode signing through repository secrets while keeping unsigned local/community builds supported.
- Automated version-tag and GitHub Release creation when a previously unreleased project version reaches `main`.
- Split the remaining roadmap into stable, versioned stages for telemetry/maps, remote portability, provider interfaces, and headless/accessibility work.

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
