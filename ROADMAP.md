# HighPop roadmap

HighPop uses two long-lived branches: `main` is production-ready and release-producing; `testing` is the integration and release-candidate line. Work branches start from and return to `testing`. Promotion to `main` requires the gates in [AUDIT.md](AUDIT.md).

## v0.8 — Testing baseline and visual system

Delivered on `testing`:

- Independent Auto-start and Always-on policies; a manual stop remains authoritative
- Framework-aware, SHA-256 verified, transactional RogueRust deployment for Oxide/uMod and Carbon
- Comment-preserving `server.cfg` synchronization with exact rollback snapshots and bounded retention
- Explicit Start, Safe Stop, Force Stop, Restart, and Install/Update command deck
- Safe Stop using `server.save` then `quit`, with a configurable 15–600 second deadline
- Safe live-update detection using installed/current Rust build IDs and configurable in-game countdown messages
- Graphite, violet, cyan, and metallic Rogue ecosystem theme applied through shared application resources
- CI coverage for both `testing` and `main`; release packaging remains `main`-only

Promotion work:

- Add disposable-process lifecycle and concurrency tests
- Complete clean-install and v0.6/v0.7 profile migration testing
- Run the Auto-start × Always-on × manager-restart/PID-reattach matrix
- Validate Windows 10/11 at 100%, 125%, 150%, and 200% DPI

## v0.8.1 — Deterministic lifecycle coordinator

- Persist desired state: operator-stopped, starting, online, maintenance, recovering, or faulted
- Give every action an operation ID, initiator, deadline, cancellation token, and durable result
- Route UI, schedules, health checks, log rules, Discord, REST, and recovery through one coordinator
- Reject duplicate Start requests and prevent Stop/Update/Restart races
- Distinguish process-running, Rust-ready, WebRCON-ready, and fresh-player-data health
- Add bounded, jittered reconnects and a redacted support bundle

Performance benefit: fewer duplicate processes and hot recovery loops, bounded waits, faster diagnosis, and no UI thread dependency for lifecycle correctness. Target unexpected-exit detection under 10 seconds and 99% of available scheduled actions starting within 30 seconds.

## v0.9 — Transactional Rust maintenance

- Stage SteamCMD updates away from the live installation, validate manifests, check free disk, and commit with rollback
- Persist update state so an interrupted manager or host restart resumes or rolls back safely
- Add player-aware maintenance windows, configurable maximum deferral, countdown cancellation, and operator override
- Broadcast update reason, remaining time, save start, shutdown, and return-to-service through Rust/RogueRust
- Reapply and verify Carbon, Oxide, RogueRust, and plugins after Rust updates when required
- Add backup verification and scheduled restore drills with recovery-point/recovery-time reporting

Performance benefit: non-destructive build-ID checks remain lightweight; downloads and validation run with bounded disk/network concurrency; update work cannot block the UI or leave half-replaced server files.

## v0.10 — Complete operator-interface overhaul

- Split the server workspace into focused Operations, Console, Rust, Automation, Mods, Players, Map, Telemetry, and Files modules
- Carry the Rogue identity through consistent typography, iconography, cards, dialogs, selection states, and progress surfaces
- Add a unified operations timeline with correlated lifecycle, update, backup, RCON, and scheduler events
- Replace raw metric density with actionable single-server and fleet dashboards
- Standardize confirmation, validation, keyboard navigation, focus order, high contrast, fullscreen, and multi-monitor behavior
- Virtualize large logs/player lists and load heavy workspaces on demand

Performance benefit: smaller view models, lower initial visual-tree cost, less retained UI state, and smoother large-server operation. Target no background-operation UI stall over 250 ms.

## v0.11 — Secure RogueRust live map

RogueRust bridge:

- Bind to `127.0.0.1` by default on a configurable dedicated port; remote binding is an explicit advanced setting
- Generate a cryptographically random 256-bit runtime API key on every Rust boot and publish it through an owner-readable local handoff file
- Authenticate every WebSocket/HTTP session, rotate on restart, use timestamped sequence numbers and message authentication to reject replay
- Require TLS through HighPop or a trusted reverse proxy for any non-loopback connection; never place keys in URLs or logs
- Add scopes for map-read, player-identity, administration, and diagnostics; map access is read-only by default
- Validate schema version, payload size, frequency, coordinates, entity type, and server identity before accepting telemetry

Map experience:

- Render the active procedural/custom map, monuments, grid, and server bounds
- Stream delta updates for players, Bradley APC, patrol helicopter, supply/transport helicopter, supply drops, locked crates, and configured event entities
- Provide role-based player-name visibility, team colours, filters, follow mode, event history, and last-update health
- Keep telemetry local by default with bounded retention; make public/remote map sharing an explicit opt-in
- Degrade safely when RogueRust disconnects: retain the last snapshot, mark it stale, reconnect with jitter, and never affect the Rust process

Performance targets: delta rather than full-state updates, coalesced UI rendering, configurable 1–5 second sampling, bounded queues, and less than 1% average Rust main-thread overhead in the test profile. A 72-hour player/entity churn soak must show no unbounded memory, queue, or disk growth.

## v1.0 — Service-grade HighPop

- Move lifecycle, scheduling, health, update, and recovery ownership into a headless Windows service
- Use authenticated local IPC with least privilege, operator roles, and tamper-evident audit records
- Add staged HighPop self-update with schema migration recovery and automatic rollback
- Publish Prometheus/OpenTelemetry metrics and versioned webhook schemas
- Add first-run NAT, firewall, SteamCMD, WebRCON, Rust+, and RogueRust diagnostics
- Support optional SFTP/FTPS backup replication and signed provider interfaces without making local management account-dependent

Performance target: a seven-day multi-server soak survives desktop-client crashes and host reboot without data loss, duplicate scheduled work, uncontrolled restart loops, or update corruption.

## Later audit backlog

- Replace unconditional five-second profile writes with dirty-state debounce and content hashing
- Remove forced full GC and system-wide working-set trimming; measure HighPop before optimizing
- Narrow firewall rules and CORS, add request limits, audit authentication failures, and separate public status from control endpoints
- Add time-zone/DST-aware schedule storage, restart catch-up, overlap suppression, and persistent countdown state
- Add notification deduplication, severity routing, quiet hours, and persistent-fault escalation
- Benchmark backup compression, log ingestion, database growth, fleet polling, and multi-server startup contention

Hosted vendor datasets and accounts remain optional. HighPop must continue to manage local Rust servers without an account, subscription, or recurring fee.
