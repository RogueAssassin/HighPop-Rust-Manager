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

- Production always-on policy that overrides empty-player shutdown and resumes with HighPop
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

## Stage 8 — v0.8 automation and remote portability

- SFTP/FTPS file-transfer profiles for remote hosts
- Import/export bundles with secrets excluded by default
- Dry-run import validation, conflict reporting, and rollback snapshots
- Scheduled off-machine backup replication without requiring a hosted HighPop account
- Event-triggered automation for crash, player-count, update, backup, and readiness events
- Maintenance windows and update deferral while players are online

## Stage 9 — v0.9 provider interfaces

- Pluggable VPN/proxy and geolocation lookups with caching and clear privacy controls
- Optional VAC/profile-risk sources that comply with provider terms
- Federated ban-list adapter with signatures, audit history, and per-list trust controls
- Prometheus/OpenTelemetry export and documented webhook event schemas

## Stage 10 — v1.0 service and experience

- More languages and accessible high-contrast themes
- First-run diagnostics for NAT, firewall, SteamCMD, WebRCON, and Rust+ ports
- Preset marketplace based on signed plain JSON bundles
- Headless Windows service mode with the desktop application acting as a client
- Role-based operator audit views and reusable per-server dashboard layouts

Hosted vendor datasets and accounts will remain optional. Local Rust management must continue to work without an account, subscription, or recurring fee.
