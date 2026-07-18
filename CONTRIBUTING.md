# Contributing

Contributions are welcome through focused pull requests.

1. Open an issue for substantial behavior or data-format changes.
2. Branch from `main` and keep unrelated refactors separate.
3. Build with the .NET 10 SDK on Windows: `dotnet build HighPop.sln -c Release`.
4. Exercise profile creation, install/update arguments, start/stop, WebRCON, backup/restore, and wipe safety for affected code.
5. Do not commit server binaries, credentials, runtime `assets/data`, backups, logs, or game installations.
6. Preserve upstream MIT attribution. Do not copy code from GPL or proprietary managers into this repository.

Rust protocol or command changes should cite an official Facepunch, Valve, Carbon, or Oxide source in the pull request.
