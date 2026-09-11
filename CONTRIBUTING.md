# Contributing

Contributions are welcome through focused pull requests.

1. Open an issue for substantial behavior or data-format changes.
2. Branch from `testing` and keep unrelated refactors separate. Pull requests for updates and new features target `testing`.
3. Promote `testing` to `main` only through a release pull request after all release gates pass. `main` is production-only and triggers release packaging.
4. Build with the .NET 10 SDK on Windows: `dotnet build HighPop.sln -c Release`.
5. Exercise profile creation, install/update arguments, start/safe-stop/force-stop, WebRCON, backup/restore, and wipe safety for affected code.
6. Do not commit server binaries, credentials, runtime `assets/data`, backups, logs, or game installations.
7. Preserve upstream MIT attribution. Do not copy code from GPL or proprietary managers into this repository.

## Branch policy

- `main`: the latest production-ready HighPop release. Direct feature work does not land here.
- `testing`: the current integration and release-candidate line. CI runs on every push.
- Short-lived branches: branch from `testing`, target `testing`, and delete after merge.
- Release promotion: open `testing` → `main`, require green Windows build/smoke/publish checks and the manual Rust lifecycle matrix, then merge and tag.

Rust protocol or command changes should cite an official Facepunch, Valve, Carbon, or Oxide source in the pull request.
