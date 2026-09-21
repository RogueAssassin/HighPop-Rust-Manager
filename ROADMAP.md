# HighPop roadmap

HighPop uses two long-lived branches: `main` is production-ready and release-producing; `testing` is the integration and release-candidate line. Work branches start from and return to `testing`. Promotion to `main` requires the gates in [AUDIT.md](AUDIT.md).

## v0.8 — Testing baseline and visual system

Delivered on `testing`:

- Independent Auto-start and Always-on policies; a manual stop remains authoritative
- Framework-aware, SHA-256 verified, transactional RogueRust deployment for Oxide/uMod and Carbon
- Comment-preserving `server.cfg` synchronization with exact rollback snapshots and bounded retention
- Case-insensitive `server.cfg` de-duplication with last-active-value precedence and one authoritative saved assignment
- Explicit Start, Safe Stop, Force Stop, Restart, and Install/Update command deck
- Safe Stop using `server.save` then `quit`, with a configurable 15–600 second deadline
- Safe live-update detection using installed/current Rust build IDs and configurable in-game countdown messages
- Graphite, violet, cyan, and metallic Rogue ecosystem theme applied through shared application resources
- CI coverage for both `testing` and `main`; release packaging remains `main`-only
- Full-fidelity close-to-tray, explicit process detachment, verified PID/start-time/path reattachment, WebRCON command fallback, and a per-user Windows logon task

Promotion work:

- Add disposable-process lifecycle and concurrency tests
- Complete clean-install and v0.6/v0.7 profile migration testing
- Run the Auto-start × Always-on × manager-restart/PID-reattach matrix
- Verify tray close, explicit exit/reopen, reattached save/stop, and Windows sign-in recovery on a real Rust process
- Validate Windows 10/11 at 100%, 125%, 150%, and 200% DPI

## v0.8.1 — Deterministic lifecycle coordinator

Implemented on `testing`; live Rust validation is in progress:

- Persisted desired state separately from observed lifecycle phase
- Added monotonic lifecycle generations, operation IDs, initiators, reasons, and timestamps
- Made Stop persist intent before process shutdown and cancel stale automatic work
- Routed local and remote lifecycle entry points through labelled coordinator operations
- Added visible lifecycle phase/reason details and migration/recovery smoke coverage
- Added deadline-aware lifecycle operations, cancellation of superseded starts, and a 32-entry durable result journal per server
- Track process-running, Rust-ready, WebRCON-ready, and fresh-player-data timestamps independently
- Added bounded exponential WebRCON reconnect cycles with jitter and lifecycle-generation cancellation
- Added a local health summary and exportable support bundle with secret redaction and bounded logs
- Added policy, signal, reconnect-bound, and redaction smoke coverage
- Added complete four-port allocation/editing for multi-server creation with profile, Windows-listener, and install-folder conflict checks
- Added source-aware, severity-filtered console reporting with batched UI ingestion and bounded duplicate tracking

Validation remaining before promotion:

- Complete the disposable Rust-process start/stop/race and injected-failure matrix on Windows
- Confirm support bundles contain enough evidence for a failed boot while never exposing credentials
- Run a slow-start/WebRCON-loss/manual-stop soak against the final `testing` head

Performance benefit: fewer duplicate processes and hot recovery loops, bounded waits, faster diagnosis, and no UI thread dependency for lifecycle correctness. Target unexpected-exit detection under 10 seconds and 99% of available scheduled actions starting within 30 seconds.

## v0.8.2 — Multi-server reliability and portable delivery

Implemented on `testing`; Windows and live Rust validation is in progress:

- Validate Game, Query, WebRCON, and Rust+ edits against all saved profiles and active listeners before accepting them
- Refuse to persist invalid legacy port collisions and safely snapshot UI-owned profile data during autosave
- Keep reattached Rust reporting alive by following `RustDedicated.log`
- Add console pause/resume, export, source/severity filtering, and recycling virtualization
- Add framework-aware Explorer shortcuts without creating inactive Oxide/Carbon folder trees
- Apply firewall and UPnP mappings as rollback-capable transactions with visible errors
- Skip unchanged periodic profile rewrites and avoid rebuilding hidden server charts
- Produce and CI-verify a portable ZIP rooted at `HPRM/`, containing `HighPop.exe`, the complete `assets/` tree, and a separate `Servers/` root
- Retain a standalone executable for in-place upgrades

Validation remaining before promotion:

- Run two simultaneous Rust servers through start, RCON, Rust+, query, safe stop, restart, and manager reattachment
- Inject firewall and UPnP partial failures and confirm no partial mappings remain
- Soak console pause/resume and reattached log rotation under high output
- Extract the CI ZIP into a clean writable location and confirm application state stays below `HPRM/assets/` while Rust installations stay below `HPRM/Servers/`

Performance benefit: unchanged profile state no longer rewrites encrypted JSON every five seconds, while console virtualization bounds visual-tree cost during long high-output sessions.

## v0.9 — Transactional Rust maintenance

Planned integration order on `testing`:

### v0.9.0 — Maintenance policy and preflight

- Add per-server maintenance windows with player thresholds, bounded deferral, operator override, quiet-hours handling, and cancellable countdowns
- Preflight disk space, install ownership, Steam build identity, backup destination, framework state, and writable rollback storage before stopping Rust
- Persist one maintenance operation ID across countdown, save, stop, stage, commit, framework verification, and return-to-service

### v0.9.1 — Staged update transaction

- Download and validate SteamCMD updates in a sibling staging directory without mutating the live server
- Commit staged files with an exact rollback manifest; reject path traversal, cross-volume non-atomic assumptions, and incomplete manifests
- Resume or roll back interrupted operations after manager/host restart, with explicit terminal results in the lifecycle journal

### v0.9.2 — Framework-safe return to service

- Snapshot and verify Carbon, Oxide/uMod, RogueRust, plugin, and config state before maintenance
- Reapply only artifacts invalidated by the Rust update, then run framework/RogueRust readiness probes before admitting players
- Broadcast reason, remaining time, save start, shutdown, rollback, and return-to-service through Rust with RogueRust enrichment when available

### v0.9.3 — Verified recovery points

- Verify every maintenance backup by reading the archive, validating its manifest/hash set, and enforcing path safety before destructive work
- Add opt-in scheduled restore drills into an isolated directory with recovery-point and recovery-time reporting
- Add retention and disk-pressure policies that never delete the last verified full recovery chain

### v0.9 release gates

- Failure injection at every transaction boundary proves either the old or new installation remains bootable
- Host/manager restart tests cover countdown, download, staging, commit, rollback, framework verification, and restart
- Multi-server tests prove disk/network/process concurrency stays bounded and one server's maintenance cannot block unrelated lifecycle work
- The full v0.8 lifecycle/manual-stop matrix remains green on the final v0.9 `testing` head

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

- Move lifecycle, scheduling, health, update, and recovery ownership from the current tray/background host into a true headless Windows service
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
