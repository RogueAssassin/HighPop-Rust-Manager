# HighPop v0.8 integration audit

Audit baseline: `6601a031cf9fe92c8259d774e361b30c9c211afb`  
Integration branch: `testing`  
Production branch: `main`

## Outcome

The v0.8 push is suitable for continued testing, not immediate production promotion. The audited head was cleanly mergeable and both Windows build-and-test jobs passed. The new `testing` branch preserves that candidate while `main` remains on the published v0.6 production line.

This audit hardened the highest-risk operator paths: normal Stop now explicitly saves then quits with a bounded configurable timeout; Force Stop saves, requests immediate exit, and enforces termination; live Install/Update is locked; safe update detection uses build IDs and configurable player countdowns; application exit honors the same stop budget; and CI now runs on `testing`.

## Findings

| Priority | Area | Finding | Disposition |
|---|---|---|---|
| P0 | Branching | Feature work and release automation shared `main`; CI did not watch `testing`. | Corrected: two-branch policy documented and CI enabled for `testing`. |
| P0 | Force Stop | The previous skull action killed the process tree without first requesting a Rust save. | Corrected: Force Stop issues `server.save`, then `quit`, waits five seconds, and kills only if still alive. |
| P0 | Safe Stop | Stop sent only `quit`, used a fixed 30-second timeout, and application exit reduced that to five seconds. | Corrected: explicit save/quit sequence, per-server 15–600 second timeout, and matching exit budget. |
| P0 | Live update | Direct Install/Update could run against files used by a live Rust process. | Corrected: direct install is locked while running; the managed update path checks build IDs and performs warned maintenance. |
| P1 | Lifecycle tests | Smoke tests validate policies and parsing but do not exercise a disposable child process through start, save, quit, timeout, force-stop, and racing requests. | Required before production promotion. |
| P1 | Operation model | UI, scheduler, health monitor, log watcher, Discord, and REST initiate lifecycle work through several orchestration paths. The manager gate serializes process operations, but there is no persisted desired-state/operation journal. | v0.8.1 lifecycle coordinator. |
| P1 | Web API | The embedded API can bind to all interfaces, creates a broad firewall rule, serves permissive CORS headers, and has no TLS termination of its own. | Keep disabled by default; harden in v0.9 before map/remote expansion. |
| P1 | Update recovery | SteamCMD validates in place. A failed update restarts the existing installation when possible, but server-binary rollback is not transactional. | Add staged depots, manifest verification, and rollback journal in v0.9. |
| P1 | Secrets | RogueRust map telemetry does not yet have a dedicated local identity, rotation, replay defense, or permission model. | Security design is a release gate for the live map stage. |
| P2 | Performance | Settings are serialized every five seconds even when unchanged. | Replace with dirty-state debounce and content hashing. |
| P2 | Performance | Optional RAM optimization forces a blocking full GC and attempts to trim every process working set on the machine. | Remove system-wide trimming; replace with measured HighPop-only policy. |
| P2 | Maintainability | `ServerViewModel` and `ServerDetailView.xaml` are both very large and combine unrelated workspaces. | Split by operations, Rust config, automation, mods, players, telemetry, and files. |
| P2 | UI scale | Fullscreen/DPI validation and virtualization are not covered by automated or manual release evidence. | Add Windows 10/11 DPI/display matrix and large-data UI benchmarks. |
| P2 | Scheduling | Update/restart countdowns are not persisted and cannot resume cleanly after manager restart. | Persist maintenance state and support cancellation/deferral in v0.9. |

## Production promotion gate

- Windows .NET 10 restore, build, smoke tests, self-contained publish, and portable-output checks pass on `testing`.
- Disposable-process lifecycle tests cover duplicate Start, Stop save/quit, timeout escalation, Force Stop, update failure, and concurrent requests.
- Manual Rust matrix covers stopped/running/starting states, manager exit, PID reattachment, Auto-start × Always-on combinations, and Oxide/Carbon/both.
- Clean install plus v0.6 and v0.7 profile upgrades preserve server data and secrets.
- Safe updater is tested with players online, an unavailable Steam service, insufficient disk, locked files, and a failed restart.
- No unresolved P0 finding; every accepted P1 risk is called out in release notes with a rollback procedure.
