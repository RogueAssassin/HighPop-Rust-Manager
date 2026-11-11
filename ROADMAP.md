# HighPop roadmap

HighPop will always manage local Rust servers without an account, subscription or recurring fee. Hosted services stay optional.

For what has already shipped, see the [changelog](CHANGELOG.md).

## Delivered so far

- **v0.8–v0.9**: a lifecycle coordinator that keeps start, stop and restart in a fixed order; safe stop (save, then quit, with a deadline); reattaching to running servers after HighPop restarts; per-server RogueRust Stable/Testing channels; the portable `HPRM` layout.
- **v1.0**: log archives, one shared SteamCMD, world-size presets, firewall controls, profile export/import and verified self-updates.
- **v1.0.1–v1.0.12**: the RogueRust plugin catalog, custom join domains with a DNS checker, a reliable WebRCON (delivery-confirmed `say`, live chat and F7 reports), Players admin tools, and a console that stays on the newest line after RDP reconnects.
- **v1.0.13–v1.0.14**: the grouped server sidebar, faster server switching, lower RAM use in the tray and a fix for chart errors after RDP reconnects. This delivers part of the v1.2 interface work below.
- **v1.0.15–v1.0.16**: Oxide and Carbon downloads verified against the SHA-256 GitHub publishes, a Discord bot that refuses actions without configured admins, a local-subnet firewall rule, token lockout and request limits for the Web API, protection against losing `servers.json`, and safer Log Watcher and health-check recovery.

## v1.1: Safer updates and recovery

- Maintenance windows with player thresholds, deferral, operator override and cancellable countdowns.
- Checks before stopping Rust: disk space, Steam build, backup destination, framework state and room for a rollback copy.
- Download Rust updates into a staging folder, swap them in with a rollback record, and resume or roll back after a crash or reboot.
- Snapshot Carbon, Oxide, RogueRust, plugins and config before maintenance, and only reopen the server once they pass health checks.
- Verify every maintenance backup before anything destructive, optional restore drills, and retention that never deletes the last good full backup.

## v1.2: Interface overhaul (partly delivered)

- ✅ Server pages grouped into a sidebar and loaded on demand.
- An operations timeline that ties lifecycle, update, backup, RCON and scheduler events together.
- Single-server and fleet dashboards that show what needs action instead of raw numbers.
- Consistent confirmations, validation, keyboard navigation, focus order, high contrast and multi-monitor behaviour.
- Virtualised large logs and player lists. Target: no UI stall over 250 ms during background work.

## v1.3: Secure RogueRust live map

- A local-only RogueRust bridge by default, with a random key per Rust boot, authenticated sessions, replay protection and TLS for any remote access.
- Read-only map access by default, with separate scopes for player identity, administration and diagnostics.
- A live map with monuments, grid, players, Bradley, helicopters, supply drops, locked crates and events, with filters, follow mode and team colours.
- Delta updates, 1–5 second sampling and under 1% Rust main-thread overhead. A 72-hour soak test must show no unbounded memory, queue or disk growth.

## v2.0: Service-grade HighPop

- Run lifecycle, scheduling, health, updates and recovery in a headless Windows service, with the window as a client.
- Authenticated local IPC, operator roles and tamper-evident audit records.
- Staged HighPop self-updates with automatic rollback.
- Prometheus/OpenTelemetry metrics and versioned webhook schemas.
- First-run checks for NAT, firewall, SteamCMD, WebRCON, Rust+ and RogueRust.
- Optional S3-compatible and SFTP/FTPS backup copies.

## Backlog

- Web API: a CORS allow-list instead of `*`, logging of failed sign-ins, optional built-in HTTPS, and separate public status and control endpoints.
- Time-zone and daylight-saving aware schedules, catch-up after downtime, overlap prevention and persistent countdowns.
- Alert deduplication, severity routing, quiet hours and escalation for persistent faults.
- Benchmarks for backup compression, log ingestion, database growth and multi-server startup.
