HighPop runtime assets
======================

HighPop Rust Manager v1.0.1

HighPop.exe is self-contained. This folder contains HighPop application state; managed Rust
installations are stored separately in the sibling HPRM/Servers folder:

HighPop checks for a newer official release when it starts and every four hours while running.
Available releases can be installed from the startup prompt, title-bar badge, or the manual
Check for update action on the About page. Downloads are SHA-256 verified before replacement.
Bundled presets are also embedded in HighPop.exe. Missing presets are restored at startup, while
custom files and locally edited presets are preserved.

Production Rust profiles use the Always-on recovery policy by default. Always-on retries an
unexpected exit only after HighPop deliberately started or reattached the server. It never starts
a stopped server merely because the manager opened; enable Auto-start for that behavior.
HighPop persists explicit desired state. A server that was deliberately running may resume
bounded recovery after HighPop reopens, while an explicit Stop invalidates every older queued
start, restart, update, wipe, health, scheduler, Discord, REST, and log-rule callback.

Safe Stop sends server.save and quit, then waits for the configured deadline before enforcing
shutdown. Force Stop still requests a save and immediate quit, but enforces exit after five
seconds. Direct Install/Update is locked while Rust is running; safe live update monitoring
checks build IDs first and broadcasts the configured countdown before maintenance.

- data/       encrypted settings, databases, schedules, update staging, and opt-in telemetry
- SteamCMD/   one shared SteamCMD installation used by every managed server
- backups/    automatic and manual server backups
- logs/       HighPop diagnostics
- logsbackup/ latest two dated Rust, Oxide, and Carbon start archives per server
- presets/    editable Rust configuration presets

Each server can export a portable .hprm-server.json settings profile. Import asks for the current
Rust installation folder so a moved server can be relinked. Server files are not embedded, and
RCON/server passwords and Discord webhooks are intentionally omitted from plaintext exports.

Before every server start or restart, completed Rust, Oxide, and Carbon logs are archived into
logsbackup/<server>/<date_time>.zip. Source logs are removed only after the ZIP succeeds.

Back up the complete HPRM folder, including HighPop.exe, assets, and Servers. Secrets are protected
with Windows DPAPI for the current Windows user. Do not publish the data folder.

The official portable ZIP has an HPRM root folder. Extract that folder as a unit so HighPop.exe
and assets remain side-by-side. Managed Rust installations now live in HPRM/Servers beside the
assets folder; existing assets/servers installations migrate automatically on first start.
