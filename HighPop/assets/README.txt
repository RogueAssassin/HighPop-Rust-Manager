HighPop runtime assets
======================

HighPop Rust Manager v0.8.0

HighPop.exe is self-contained. This folder is the only external location HighPop uses:

Production Rust profiles use the Always-on recovery policy by default. Always-on retries an
unexpected exit only after HighPop deliberately started or reattached the server. It never starts
a stopped server merely because the manager opened; enable Auto-start for that behavior.

Safe Stop sends server.save and quit, then waits for the configured deadline before enforcing
shutdown. Force Stop still requests a save and immediate quit, but enforces exit after five
seconds. Direct Install/Update is locked while Rust is running; safe live update monitoring
checks build IDs first and broadcasts the configured countdown before maintenance.

- data/       encrypted settings, databases, schedules, update staging, and opt-in telemetry
- servers/    Rust dedicated server installations
- backups/    automatic and manual server backups
- logs/       HighPop diagnostics
- presets/    editable Rust configuration presets

Back up HighPop.exe and this assets folder together. Secrets stored by HighPop are protected
with Windows DPAPI for the current Windows user. Do not publish the data folder.
