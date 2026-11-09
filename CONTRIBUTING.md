# Contributing

Contributions are welcome through focused pull requests.

## Build from source

Requirements: Windows 10 / Server 2019 or newer and the .NET 10 SDK.

```powershell
git clone https://github.com/RogueAssassin/HighPop-Rust-Manager.git
cd HighPop-Rust-Manager
dotnet restore HighPop.sln
dotnet build HighPop.sln -c Release
dotnet run --project HighPop.SmokeTests/HighPop.SmokeTests.csproj -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-HighPop.ps1
```

`Build-HighPop.ps1` creates the self-contained executable, the portable ZIP, checksums and the release manifest under `artifacts/`.

If Windows PowerShell says `Build-HighPop.ps1` is not digitally signed, allow scripts for the current window only, then run it again:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
.\Build-HighPop.ps1
```

The `Process` scope ends when the window closes. Do not set `LocalMachine` to `Unrestricted` to build HighPop.

## Making a change

1. Open an issue first for large behaviour or data-format changes.
2. Branch from `main` and keep unrelated refactors out of the pull request.
3. Run the smoke tests and `scripts/Test-UiSurface.ps1` (checks every XAML view and command binding) before opening the pull request. CI runs both.
4. For lifecycle, WebRCON, backup or wipe changes, also try the affected flow on a real Rust server: start, safe stop, force stop, restore or wipe.
5. Add a line to `CHANGELOG.md` for anything users will notice.
6. Do not commit server binaries, credentials, runtime `assets/data`, backups, logs or game installations.
7. Keep the upstream MIT attribution. Do not copy code from GPL or proprietary managers.

Rust protocol or command changes should cite an official Facepunch, Valve, Carbon or Oxide source in the pull request.

## Releases

A push to `main` builds, tests and packages HighPop, then publishes a GitHub release tagged with the version in `HighPop/HighPop.csproj`. Bump that version (and the version check in `HighPop.SmokeTests/Program.cs`) in the same change as the changelog entry.
