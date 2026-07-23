HighPop runtime assets
======================

HighPop.exe is self-contained. This folder is the only external location HighPop uses:

- data/       encrypted settings, databases, schedules, update staging, and opt-in telemetry
- servers/    Rust dedicated server installations
- backups/    automatic and manual server backups
- logs/       HighPop diagnostics
- presets/    editable Rust configuration presets

Back up HighPop.exe and this assets folder together. Secrets stored by HighPop are protected
with Windows DPAPI for the current Windows user. Do not publish the data folder.
