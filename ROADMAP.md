# Roadmap

HighPop releases are staged around stability and self-hostability. A stage is merged only when its Windows build, smoke tests, and portable publish checks pass.

## Completed — v0.4 configuration and release integrity

- Identity-level `server.cfg` read/write synchronization with comment preservation
- Safe one-time migration from HighPop's v0.3 managed `serverauto.cfg` block
- Direct Windows executable and portable ZIP release assets
- Per-file SHA-256 checksums and a source-linked JSON release manifest
- Optional Authenticode signing hook for repositories with a configured certificate

## Completed — Stage 5 / v0.5 telemetry foundation

- Versioned, opt-in local event stream for lifecycle, readiness, operator actions, and player-count changes
- Per-server age and storage retention controls so local telemetry cannot grow without bounds

## Completed — Stage 6 / v0.6 always-on operations

- Production always-on policy that overrides empty-player shutdown and recovers deliberately started or reattached servers
- Persistent recovery after unexpected exits and failed relaunches
- Shared crash history with capped exponential backoff to prevent hot restart loops
- Durable, serialized scheduler execution with visible results, failure counts, and timings
- Daily, weekly, interval, and one-time scheduling with persisted next-run repair
- Corrupt scheduler-state quarantine instead of manager instability

## Completed — Stage 7 / v0.7 Rust workspace and managed extensions

- Observable, filterable `server.cfg` variable workspace with pending-change visibility
- Automatic rollback copies and comment-preserving atomic config writes
- Save-and-apply-live workflow for enabled Rust console variables
- Verified RogueRust GitHub installation/update with version and readiness diagnostics
- Clearer visual hierarchy, status pills, spacing, and managed-mod presentation

## Stage 8 — v0.8 safe policy and managed extensions (in progress)

Delivered in the first v0.8 slice:

- `Auto-start` and `Always-on` are independent: loading HighPop cannot start a stopped always-on profile
- Framework-aware RogueRust deployment for Oxide/uMod and Carbon, including verified transactional replacement and automatic rollback
- Exact `server.cfg` rollback snapshots with bounded retention and documented recovery
- Unified HighPop icon, banner, splash, and in-app wordmark aligned with the Rogue ecosystem

v0.8 release gates:

- Windows build, smoke tests, and self-contained publish all pass at the final commit
- Oxide-only, Carbon-only, and failed dual-target RogueRust scenarios are verified
- All four `Auto-start` / `Always-on` combinations are verified across manager restart, explicit stop, crash, and PID reattachment
- Clean-install and v0.6/v0.7 upgrade tests pass with no stale version or policy text in the portable package

## Stage 9 — v0.8.1 QoS baseline and lifecycle observability

- Model explicit desired states: stopped by operator, starting, online, recovering, maintenance, and faulted
- Separate process-running, Rust-ready, WebRCON-ready, and fresh-player-sample health signals
- Use bounded, jittered WebRCON reconnects and record every automatic recovery decision
- Harden schedules for duplicate suppression, restart catch-up, time-zone/DST changes, and overlapping tasks
- Add global/per-server limits and CPU, disk, and network throttles for backup, update, and replication work
- Export a redacted support bundle covering lifecycle, scheduler health, ports, recent logs, and configuration state

Quality targets:

- Manual stops remain stopped until an explicit start trigger
- Every changed config has a verified restorable predecessor
- 99% of due scheduled actions begin within 30 seconds when the host and server gate are available
- Unexpected exits are detected within 10 seconds without a hot restart loop
- Downloads, backups, updates, log ingestion, and fleet polling do not block the UI thread

## Stage 10 — v0.9 production-safe automation and portability

- Maintenance windows, player-aware update deferral, countdown broadcasts, cancellation, and maximum deferral
- Resumable update, backup, wipe, restore, framework-update, and config-deployment workflows with preflight and rollback
- Event-triggered automation for crash, readiness, player thresholds, backup failure, update availability, and disk pressure
- SFTP/FTPS transfer profiles, safe import/export bundles, dry-run conflict reporting, and off-machine backup replication
- Backup verification and scheduled restore drills with recovery-point and recovery-time reporting
- Notification deduplication, severity routing, quiet hours, and persistent-fault escalation
- Opt-in live map workspace fed by an authenticated RogueRust telemetry bridge

Release gate: a 72-hour fault-injection soak with manager/host restarts produces no data loss, duplicate scheduled action, or uncontrolled restart loop.

## Stage 11 — v0.10 operator experience and accessibility

- Validate fullscreen, multi-monitor work areas, and 100–200% DPI without clipped controls
- Standardize dialogs, validation summaries, confirmations, keyboard navigation, focus order, and high-contrast states
- Correlate lifecycle, RCON, scheduler, config, backup, update, and notification events in one operations timeline
- Provide actionable single-server and fleet dashboards instead of raw metric overload
- Benchmark and optimize large logs, long player lists, many schedules, and multi-server polling

Release gate: Windows 10/11 display matrix passes, critical workflows work keyboard-only, and background operations cause no UI stall longer than 250 ms.

## Stage 12 — v1.0 service-grade architecture and provider interfaces

- Move lifecycle, scheduler, health, and recovery ownership into a headless Windows service with a reconnecting WPF client
- Add authenticated local IPC, least-privilege service configuration, operator roles, and tamper-evident auditing
- Add staged self-update, schema migration recovery, and automatic rollback to the last healthy manager build
- Add pluggable VPN/proxy, geolocation, VAC/profile-risk, and signed federated ban-list providers with caching and privacy controls
- Publish Prometheus/OpenTelemetry export and documented webhook event schemas
- Add first-run NAT, firewall, SteamCMD, WebRCON, and Rust+ diagnostics

Release gate: a seven-day multi-server soak survives desktop-client crashes and host reboot; a failed manager upgrade automatically returns to the last healthy build.

Hosted vendor datasets and accounts will remain optional. Local Rust management must continue to work without an account, subscription, or recurring fee.
