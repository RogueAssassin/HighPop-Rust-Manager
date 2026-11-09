# Changelog

## 1.0.14

- Fixed "This PlotModel is already in use by some other PlotView control" errors after logging back in over RDP. When the server page was rebuilt, the old Charts page still held the server's charts, so the new one could not show them. The Charts page now takes the charts only while it is on screen and releases them when hidden.
- Rewrote the README with screenshots of the server workspace and dialogs. Moved the detailed reference (WebRCON, players, configuration, mods, storage layout) to `docs/`, removed the outdated v0.8 audit and refreshed the roadmap and contributing guide.
- Advanced metadata to v1.0.14.

## 1.0.13

- Reworked the server page. The 14 tabs are now six groups in a sidebar: Console, Players, Configure, Automation, Monitoring and Files, each with its pages along the top. The colours are calmer and the active page is easier to see.
- The server page loads faster and switching servers is about five times quicker (about 15 ms instead of 65–100 ms in testing). Each page is now built the first time it is opened instead of all 14 at once. Switching servers only refreshes the page on screen.
- Charts are redrawn only while the Charts page is open.
- Once the console holds 2,000 lines, dropping the oldest line no longer searches the whole filtered list, which cuts UI work on busy servers.
- When HighPop has been minimised or in the tray for 10 seconds it compacts memory and returns idle pages to Windows. In testing its RAM use while minimised dropped from about 205 MB to 55 MB. Opening the window again brings back only what is shown.
- Removed a hidden copy of the automation settings from the Settings page. Everything it held is on the Automation page.
- Fixed servers being duplicated on every launch when `servers.json` contained a server with a missing, invalid or repeated Id (for example after hand editing). The repaired Ids are now saved straight away.
- Updated Microsoft.Data.Sqlite and Microsoft.Extensions.DependencyInjection to 10.0.14, and removed the unused Microsoft.CodeAnalysis.CSharp package. No known vulnerable or deprecated packages remain.
- Advanced metadata to v1.0.13.

## 1.0.12

- Added a **Command / 💬 Chat** switch next to the Console input. In Chat mode, whatever you type goes to every player as in-game chat. In Command mode the box runs Rust console commands as before, so chat needs `say` first, for example `say This is a test`.
- Typing plain text in Command mode used to be sent to Rust as a command named after its first word. Rust ignores unknown commands without replying, so the console only showed "No response from the Rust server within 8s". HighPop now checks the first word against the Rogue Rust Reference before sending and says straight away that it is not a Rust command, and how to message players instead. The line is still sent in case a plugin handles it. Carbon, Oxide and Harmony commands (`c.`, `o.`, `carbon.`, `oxide.`, `umod.`, `harmony.`) are not flagged.
- Advanced metadata to v1.0.12.

## 1.0.11

- Fixed in-game `say` from the Console tab not reaching players. Rust ignores commands written to its standard input, but HighPop used that input whenever the console was not connected to WebRCON, and logged the message as sent. Rust commands now always go over WebRCON. The console connects on demand, and the process console is only a last resort, with a warning that it may not arrive.
- `say` is now confirmed: Rust broadcasts every chat line to WebRCON clients, so the console reports **Delivered in game**, or warns when Rust accepted the message but did not broadcast it (for example, a plugin blocking `OnServerMessage`).
- Fixed WebRCON failing for RCON passwords containing symbols such as `! $ + = @`. HighPop percent-encoded the password, but Rust compares the raw request path without decoding it. Passwords are now sent as typed. Passwords a URL cannot carry at all (spaces, `" # % / ? \` and non-ASCII) are reported with the reason instead of being retried every few seconds. WebRCON connection errors now say why they failed.
- Background commands (scheduled broadcasts, restart warnings, Web API, Discord and stop requests) also use WebRCON first for Rust.
- Removed the `+rcon.enabled 1` launch argument; Rust has no such command (the server log reported it as not found).
- Players tab:
  - A live server strip from `serverinfo`: players, queue, joining, server FPS, entities, memory, uptime and in-game time.
  - Player rows now show health, IP address, Steam Family Sharing (with the owner's account) and the anti-hack violation level, with buttons to open the Steam profile, copy the Steam64 ID and choose a teleport destination.
  - New **Player tools** tab:
    - A give menu: 131 common items in categories with search, or any short name, with an amount, quick amounts and an optional skin ID. Items go to the selected player, or to everyone online after a confirmation.
    - Mute and unmute, team info, unlocking or resetting blueprints, clearing an inventory, kill, and teleporting one player to another.
    - Adding or removing moderators (saved with `server.writecfg`) and queue skip.
    - Saved loadouts, and the mute and Rust ban lists.
    - Rust's reply to each command is shown in the panel. Destructive actions ask for confirmation first.
  - New **Chat & reports** tab: live in-game chat over WebRCON (starting with the last 50 lines), a message-all box with delivery confirmation, and F7 player reports as they arrive (reports are also written to the console).
  - Every tool targets a player's Steam64 ID when it is known, quotes names, and validates item short names, amounts and loadout names, so typed values cannot inject a second console command.
- Smoke coverage: the fake WebRCON server now compares the raw password path the way Rust does, using a password with symbols, and broadcasts `say` as chat. New tests cover `say` delivery and blocked messages, the chat relay, password validation, the player-tool command builders, the item catalog, the `serverinfo`, `playerlist` and F7 report parsers, and numeric broadcast types. Advanced metadata to v1.0.11.

## 1.0.10

- Fixed the Console not following new output after reconnecting over Remote Desktop, unlocking the session or restoring the window: with **⬇ Auto scroll** on, it now re-renders and jumps to the newest line on its own instead of needing a manual scroll.
- Auto scroll now scrolls the console's own scroll viewer to the end after layout, once per batch of lines, instead of calling `ScrollIntoView` for every line. This keeps it at the true end with long wrapped lines, filter changes, the 2,000-line trim and window resizes, and cuts the UI work during heavy log bursts.
- Advanced metadata to v1.0.10.

## 1.0.9

- Added custom join domain support. **Server → Settings → Public join domain** (for example `rust.example.com`) launches Rust with `+server.favoritesEndpoint`, so favourites follow the domain through IP and port changes, as Facepunch documents.
- Added **Server → 🦀 Rust → Custom join domain → Check DNS**. It queries Cloudflare DNS-over-HTTPS for the A and `_rust._udp` SRV records and compares them with this network's public IPv4. It reports names that do not exist, Cloudflare-proxied (orange-cloud) addresses, private or CGNAT addresses, IP mismatches, missing or duplicate SRV records, and SRV ports that are not the game port, and shows the `client.connect` command to share.
- Added [docs/custom-domains.md](docs/custom-domains.md): Cloudflare, GoDaddy, Namecheap, Porkbun, Route 53, Google Cloud DNS and BIND setup, router ports, dynamic-IP updates through the Cloudflare API, hairpin-NAT testing and troubleshooting.
- Invalid public join domains are rejected before Rust starts.
- Fixed the v1.0.8 CI and release packaging check, which still required the retired `rust_vanilla_1000`/`6000` presets.

## 1.0.8

- Retired the eleven `Vanilla map 1000`–`6000` presets. Rust already scales every `*.population` convar (animals, livestock, sea life, birds, vehicles, trains) by map area, so the presets only replaced vanilla defaults with inflated values (for example 12 rowboats and 5 minicopters per km²) and wrote six `npc.vendor_*` convars that do not exist in Rust. **Vanilla community** and **High-population community** remain.
- Existing installs remove the retired presets on startup only while they are byte-identical to the copy HighPop deployed; a preset you edited is kept as a custom preset.
- New servers no longer start with `bear.population` and `wolf.population` rows (`wolf.population` was replaced by `wolf2.population` in Rust). Untouched seed rows are dropped from existing servers unless `server.cfg` sets them.
- Fixed `server.cfg` inline comments leaking into the variable editor: `bear.population "2"   # ~50` now reads as `2`. Trailing `#`/`//` comments are ignored after a quoted value, or after whitespace in an unquoted one, and doubly-quoted values such as `"\"1\""` read as `1`.
- Saving the Rust workspace now writes every active line as `name "value"` with no inline comment; an existing inline comment is moved to its own line directly above.
- CI and release packaging verify the retired presets are no longer shipped. Added smoke coverage for comment/quote parsing against real-world lines, canonical save output, seed-row cleanup and safe preset retirement. Advanced metadata to v1.0.8.

## 1.0.7

- Fixed in-game chat over WebRCON lagging or appearing to hang. Rust sends no correlated reply to `say`, so every message waited out the full 10-second response timeout. Commands that never reply (`say`, `global.say`, `chat.say`, `quit`) now complete as soon as they are sent.
- Removed the server workspace lock that was held across every WebRCON round-trip. Console commands, quick commands, moderation actions and the player-list refresh now run concurrently, with replies matched by identifier, instead of queuing behind a slow or reply-less command.
- The Console **Send** and quick-command actions no longer disable themselves while a reply is pending, so several messages can be sent back to back.
- Background commands (scheduled broadcasts, restart/update warnings, Web API, Discord, log-watch actions and Oxide save/quit) reuse one persistent WebRCON channel per running server instead of opening and authenticating a new WebSocket for each command, and reconnect once automatically if Rust closed it.
- WebRCON replies to background commands on Oxide servers (such as RogueRust diagnostics) now appear in the Console tab instead of being discarded.
- WebRCON connections are verified with a read-only `serverinfo` probe, so a wrong RCON password reports a failed connection instead of looking connected until the first command.
- Hardened the WebRCON client: positive-only request identifiers (Rust reserves 0 and -1), single-frame receive fast path with a reused buffer, an 8 MB message cap, IPv6 host formatting, and idempotent disposal.
- Added the **RogueRust plugin catalog** to Server → Mods, backed by `plugins.roguegaming.com.au`: browse all published plugins by category, see installed vs. current versions, open documentation, and install/update a plugin into the active Oxide/Carbon plugins folder with host-locked HTTPS downloads, `[Info]` validation, atomic replacement, and five rollback copies per plugin.
- Centralised the Rogue Gaming hosts (`api.roguegaming.com.au`, `plugins.roguegaming.com.au`) with exact-host HTTPS checks shared by the Rust Reference and plugin catalog.
- Added smoke coverage for reply-less WebRCON commands, wrong-password rejection, concurrent reply correlation against a loopback WebRCON server, plugin catalog parsing/trust rules, `[Info]` version detection, and endpoint host checks. Advanced application and documentation metadata to v1.0.7.

## 1.0.6

- Fixed Oxide stops appearing frozen by keeping **Force Stop PID** available throughout the `Stopping` phase and allowing it to cancel the active graceful wait.
- Captured the exact Rust PID and process start time at launch/reattach, then used that verified identity for bounded process-tree termination and the Windows `taskkill /T /F` fallback without risking a recycled PID.
- Prevented Start and Install/Update from becoming available after a failed stop while the tracked Rust PID is still alive; Force Stop remains available for recovery.
- Reduced Force Stop's command/exit grace window so an unresponsive Oxide server reaches forced termination quickly while still receiving `server.save` and `quit` first.
- Added an emergency PID termination path when a stuck graceful-stop operation does not release its lifecycle lock, and moved process-tree killing off the UI thread.
- Reworked SteamCMD bootstrap downloads to stream directly to disk instead of buffering the ZIP in memory, with byte progress, extraction status, staged Rust download/verification progress, elapsed-time heartbeats, and completed-process disposal.
- Kept SteamCMD UI updates asynchronous, coalesced network/chart rendering, and reduced active performance-chart refreshes to five-second intervals while retaining two-second metric history samples.
- Improved Settings with clearer storage/runtime guidance, inline save/open status, Explorer shortcuts for server, backup, and shared SteamCMD folders, and validation for Web API/SMTP ports, health thresholds, and CPU/memory percentages.
- Added smoke coverage for SteamCMD progress formats and manager-owned PID identity validation, and advanced testing metadata to v1.0.6.
- Fixed the whole manager freezing ("Not Responding") when Stop or Force Stop was used on an Oxide server. The one-shot WebRCON client used for `server.save`/`quit` was disposed with a blocking wait on the UI thread, whose async close could only finish on that same thread. RCON teardown is now non-blocking, all RCON awaits use `ConfigureAwait(false)`, and Start/Stop/Force Stop run off the UI thread.
- Added a shared `UiDispatcher` helper and replaced blocking `Dispatcher.Invoke` calls in status, lifecycle, player, RCON, mod, remote-machine and settings handlers with non-blocking dispatch, so background events can never stall waiting on the UI.
- Background services (process events, Web API, Discord, scheduler, crash prediction, auto-save) now read an immutable server snapshot instead of marshalling to the UI thread, removing a blocking UI round-trip for every console line.
- Removed blocking waits from manager exit and Settings: the Discord bot (up to 6 s), remote-machine polling (up to 8 s), crash prediction (3 s) and RCON disposal (3 s) no longer hold the window, and Web API/firewall registration plus the Windows startup task now run in the background.
- Restarts (manual, scheduled, daily and wipes) now wait for the old Rust process to exit and its game/query/RCON ports to free (up to 30 s) instead of a fixed 3-second delay, and a manual restart is cancelled if the server is still running after the stop attempt.
- Pre-flight cleanup of leftover `RustDedicated.exe` processes is now asynchronous and disposes every process handle it inspects.
- Added a smoke test that reproduces the Oxide stop deadlock (disposing a connected WebRCON client on a blocked UI-style thread).

## 1.0.5

- Fixed the Reference result template's read-only `DefaultText` binding repeatedly throwing WPF dispatcher exceptions when command/ConVar results became visible, and added a non-modal recovery guard plus CI audit for inline `Run.Text` bindings.
- Fixed Oxide/uMod shutdowns becoming stuck in `Stopping` when redirected standard input is intentionally disabled and WebRCON cannot complete a clean exit.
- Made Force Stop interrupt an active graceful-stop wait immediately instead of queuing behind its full timeout.
- Retained live process identity until Rust is confirmed exited, added bounded process-tree retries plus a Windows `taskkill` fallback, and kept failed terminations attached so administrators can retry without restarting HPRM.
- Added an on-demand Rogue Rust Reference tab backed by `api.roguegaming.com.au`, with searchable command/ConVar metadata, execution scopes, descriptions, defaults, safe console insertion, and an offline cache.
- Advanced application, portable package, documentation, and smoke-test metadata to v1.0.5.

## 1.0.4

- Fixed the repeated WPF/DirectWrite `TextAnalyzer.GetGlyphs` crash loop reported by v1.0.3 when a virtualized console-style list receives text Windows cannot shape safely.
- Added a shared UI text boundary that replaces malformed UTF-16 surrogate data, removes unsafe controls, and caps individual rendered console lines at 16 KiB while leaving authoritative Rust logs on disk untouched.
- Simplified console shaping to the Windows-provided Consolas font with kerning and standard ligatures disabled, reducing font-fallback work under heavy server output.
- Added non-modal, rate-limited recovery for DirectWrite text failures: HPRM quarantines only its in-memory console/audit display entries instead of producing a repeated error-dialog storm or affecting the Rust process.
- Added smoke coverage for malformed UTF-16 and oversized plugin output, and advanced testing metadata to v1.0.4.

## 1.0.3

- Added the Rust Livestock Update's verified `cow.population` and `sheep.population` controls to every vanilla map preset from 1000 through 6000 while retaining every existing preset value.
- Kept livestock population independent of player slots: both controls use Rust's per-square-kilometre density model, so total herds scale naturally with map area.
- Extended bundled preset upgrades to merge newly introduced keys into operator-edited built-in presets without replacing or removing any existing local value.
- Reduced idle monitoring overhead by refreshing fixed-drive capacity every 30 seconds instead of every two seconds and preventing overlapping system/process metric samples.
- Advanced application, package, documentation, and smoke-test metadata to v1.0.3.

## 1.0.2

- Replaced command-local WebRCON reads with a continuous receive loop and request-ID correlation, keeping unsolicited Rust console/chat packets separate from command responses like established Rust RCON clients.
- Switched online-player polling to the fully qualified `global.playerlist` command and retained WebRCON as the authoritative source for player identities, Steam IDs, ping, and connected time.
- Hardened player-list parsing for wrapped, nested, case-insensitive, prefixed, numeric-ID, proxy-alias, and empty-array responses without weakening malformed-payload rejection.
- Made a dropped player-query connection immediately update the visible WebRCON state and enter the existing bounded reconnect workflow instead of remaining falsely connected.
- Kept Steam A2S as a fallback only when WebRCON cannot provide a readable player list, and removed the remaining nullable firewall build warning.

## 1.0.1

- Fixed in-app updates installing only `HighPop.exe` and omitting new external assets such as the eleven map-size presets.
- Embedded the shipped presets in the executable so existing v1.0.0 installations automatically restore missing preset files on their first v1.0.1 start.
- Added managed preset hash tracking: missing and unchanged built-in presets update automatically, while custom and locally edited presets remain untouched.
- Updated the self-updater to validate and stage `HPRM/assets/**` from the release ZIP and merge missing packaged assets without deleting runtime data or operator-created files.
- Made preset application retain exactly one active assignment per managed `server.cfg` key, comment older duplicates for auditability, and synchronize HighPop's variable workspace before the next launch.
- Moved Vanilla console reporting to the same single authoritative `RustDedicated.log` path as Oxide; Carbon retains its richer process-stream console.
- Hardened online-player discovery with case-insensitive/wrapped WebRCON payload parsing, tolerant response identifiers, serialized refreshes, Steam A2S fallback, reconnect handling, and a visible player-query status.

## 1.0.0

- Promoted the completed testing work as the v1 stable HighPop Rust Manager release line.
- Added eleven editable vanilla map presets from 1000 through 6000 in 500-unit increments, with the independent 150-player vanilla default, fixed wildlife densities, area-scaled vehicle targets, vendor defaults, and synchronized profile/launch settings.
- Made preset ordering deterministic, expanded the preset UI guidance and new-map warning, and added build/package checks for the full preset set.
- Hardened Windows Firewall setup with an explicit elevation preflight, friendly Windows service/Group Policy/COM diagnostics, bounded rule names, COM cleanup, per-server opt-out controls, and warning-level reporting when elevation is the only blocker.
- Added per-server `.hprm-server.json` export/import, import-time relinking for moved Rust installations, collision-safe port reassignment, new profile identities, runtime-state reset, schema validation, and explicit exclusion of plaintext credentials.
- Added transactional start/restart log archiving for Rust, Oxide, and Carbon. Completed logs are zipped beneath `assets/logsbackup/<server>/` with date-and-time filenames, source files are removed only after the ZIP succeeds, and only the latest two start archives are retained per server.
- Centralized the shared SteamCMD installation at `assets/SteamCMD` for every managed server, including automatic migration from the previous `assets/data/steamcmd` location.
- Updated the source-build guide with a safe process-scoped PowerShell execution-policy bypass for unsigned local build scripts.
- Confirmed the self-contained .NET 10 Windows x64 build for Windows 10/11 and Windows Server 2012 through 2025 where the selected .NET/Windows lifecycle combination remains supported; Windows 7 and Windows Server 2008 R2 are not supported by the current runtime.

## 0.9.0

- Promoted the tested multi-server, lifecycle, portable-layout, RogueRust channel, performance, and framework-management work to the production release line.
- Added an explicit startup update prompt while retaining four-hour background checks, the title-bar update badge, and the manual About-page check.
- Kept in-place updates SHA-256 verified, with graceful running-server shutdown, executable replacement, automatic restart, failed-swap detection, and a manual-release fallback.
- Fixed Oxide console duplication at its source by launching it with valid hidden Windows console handles and using one fresh `RustDedicated.log` feed; Carbon retains its process-stream console.
- Corrected Oxide version reporting to use the current Rust compatibility extension and pinned framework installation to uMod's public Rust channel.
- Finalized the minimal `HPRM/HighPop.exe`, `HPRM/assets/**`, and `HPRM/CHANGELOG.md` portable release package.

## 0.8.5

- Added a persisted per-server RogueRust release channel: Stable for production servers and Testing for development servers.
- Switched RogueRust installation to the public Stable/Testing update manifests, with channel validation, trusted release URL validation, SHA-256 verification, transactional installation, and rollback.
- Pass the selected RogueRust manifest into each Rust process so extension self-updates remain on the server's chosen channel after restart.
- Added channel-aware status, action logging, guidance, cloning, and smoke coverage.
- Reduced the portable ZIP to `HPRM/HighPop.exe`, `HPRM/assets/**`, and `HPRM/CHANGELOG.md`, with CI rejecting unexpected packaged files.
- Refocused the README on HighPop's current capabilities, installation, layout, security, RogueRust channels, and operator workflow; release history remains in this changelog.
- Reviewed current manager capabilities and added the remaining high-value operational gaps to the roadmap.
- Removed the legacy “optimize system RAM” launch option, which forced a blocking full garbage collection and trimmed every Windows process working set before starting Rust; Windows and .NET now manage memory normally without the launch pause or system-wide churn.
- Fixed Oxide console reporting by de-duplicating lines mirrored across stdout, stderr, and `RustDedicated.log`, while retaining legitimate repeated messages from the same source.
- Added live `RustDedicated.log` tailing as a console fallback so Oxide output remains visible when its redirected process streams are incomplete.
- Pinned Oxide installation to uMod's official latest public Rust channel (`download?tag=public`) and retained archive-content validation before extraction.
- Corrected Oxide detection to report the current `Oxide.Rust.dll` compatibility version instead of the independently versioned Oxide Core assembly.
- Split live console ingestion by framework: Carbon retains its formatted process-stream console, while Oxide switches to the authoritative live `RustDedicated.log` feed as soon as it becomes available.
- Stopped redirecting stdout and stderr for Oxide launches after confirming that Oxide mirrored those redirected streams back into `RustDedicated.log`; each launch now preserves the previous log and consumes one fresh logfile from byte zero.
- Gave Oxide a real hidden Windows console with no redirected standard handles, eliminating its `ERROR_NOT_SUPPORTED` output fallback that registered duplicate logfile reporting; Oxide commands now use WebRCON while Carbon retains redirected console input/output.

## 0.8.2

- Expanded new-server creation so Game, Query, WebRCON, and Rust+ ports are editable, checked against saved profiles and active Windows listeners, and can be reassigned together.
- Added the same conflict validation to existing profiles, blocked persistence of invalid legacy collisions, prevented shared installation directories, and assigned verified free ports to clones.
- Reworked console ingestion with bounded UI batches, source and severity parsing, ANSI cleanup, pause/resume, export, recycling virtualization, and bounded duplicate tracking.
- Restored server reporting after process reattachment by tailing the active Rust log with truncation and rotation handling.
- Made firewall and UPnP port setup transactional, including rollback and visible failure reporting; UPnP now includes WebRCON.
- Avoided unchanged encrypted profile rewrites during periodic autosave and captured UI-owned profile state safely.
- Added a shared portable-package builder and CI validation. The ZIP now extracts to `HPRM/HighPop.exe` and `HPRM/assets/**`, while the standalone executable remains available.
- Moved managed Rust installations to `HPRM/Servers/**`, with conflict-safe migration from the former `HPRM/assets/servers/**` layout and automatic saved-path repair.
- Corrected new-server paths to `HPRM/Servers/<server-name>` without an intermediate `rust` folder, and safely flattened paths created by the earlier v0.8.2 testing build.
- Added server-specific Windows Explorer shortcuts for server files, Rust configuration/logs, and the active framework's root, plugins, configs, and extensions.
- Made mod-folder controls framework-aware: Carbon never exposes or creates Oxide folders, Oxide never exposes or creates Carbon folders, and ambiguous dual-framework installs expose neither.
- Reduced idle UI and serialization work by rebuilding live performance charts only for the selected server and changing unchanged-profile autosave checks from five to fifteen seconds; application shutdown still saves immediately.
- Fixed unobserved WebRCON shutdown faults by replacing the timed-out synchronous close handshake with an awaited, cancellation-bounded close-output operation that observes remote disconnect errors.
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
