<p align="center">
  <img src="highpop-banner.png" alt="HighPop Rust Manager" width="760">
</p>

<p align="center">
  A portable, local-first Windows control plane for serious Rust communities.
</p>

<p align="center">
  <img alt="Version" src="https://img.shields.io/badge/version-0.4.0-F05A28">
  <img alt=".NET" src="https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet">
  <img alt="Platform" src="https://img.shields.io/badge/platform-Windows%20x64-0078D4?logo=windows">
  <img alt="License" src="https://img.shields.io/badge/license-MIT-38C976">
</p>

HighPop Rust Manager brings installation, lifecycle control, administration, automation, monitoring, backups, mods, and remote access into one native Windows app. The release is a self-contained `HighPop.exe`; mutable files live under `HighPop/assets/**`, so a server installation can be moved or backed up as a unit.

> HighPop is an independent community project. It is not affiliated with or endorsed by Facepunch Studios, Valve, Rustadmin, MyRustServer, CFTools, or EU Game Host.

## Included in v0.4

| Area | Capabilities |
|---|---|
| Rust installation | One-click SteamCMD bootstrap, install, validate, update, public-branch support, update-on-start |
| Process control | Start, graceful stop, restart, crash recovery, crash-loop protection, serialized scheduler operations, daily restarts, idle shutdown, wake on demand |
| High-pop profiles | 500-player default, vanilla/modded/development labels, Facepunch-documented browser tag picker, `server.cfg` variable synchronization, custom log directory, identity and conflict-safe ports |
| Administration | Auto-reconnecting Facepunch WebRCON console, native timed bans, kick/unban, persistent player notes, whitelist permissions, confirmation-gated bulk moderation, shared group bans, and richer session statistics |
| Mods and maps | Carbon and Oxide installation/detection, installed-plugin inventory, HTTP(S) custom-map URL, server/plugin config discovery, version history, and live plugin reload |
| Wipes and backups | Map/full wipes, mandatory pre-wipe safety backup, full/incremental ZIP backups, retention, restore with path-traversal protection |
| Automation | Once/daily/weekly/repeating tasks for start, stop, restart, update, backup, wipe, broadcast, and console commands |
| Monitoring | Persistent CPU/RAM/network/player graphs, system metrics, bandwidth, player activity, health checks, log watches, crash-risk warnings, server hygiene, and an expanded action trail |
| Remote operations | Optional token-protected REST API, browser dashboard, status links, master/slave machines, a local Discord bot with live boards and staff controls, webhooks, and SMTP alerts |
| Windows controls | System tray, startup registration, CPU affinity, process priority, optional RAM cap, firewall and UPnP controls |
| Customization | Portable Rust presets, editable launch/config values, server templates, custom images, and replaceable brand assets |

HighPop deliberately does not copy proprietary hosted databases or subscription services. VAC/VPN intelligence, globally shared ban data, and hosted web accounts require external data providers; the local manager remains usable without an account or recurring fee. See [ROADMAP.md](ROADMAP.md) for planned provider interfaces and deeper Rust telemetry.

## Install

1. Download the latest Windows x64 ZIP from [Releases](../../releases).
2. Verify its matching `.sha256` file, then extract the entire `HighPop` folder to a writable location.
3. Run `HighPop.exe`.
4. Add a Rust server profile and choose **Install**. SteamCMD is downloaded automatically.

The release also includes a directly downloadable `HighPop-<version>-win-x64.exe`. It is useful for replacing an existing installation while retaining its `assets` folder; the ZIP remains the recommended first installation because it includes editable presets and documentation. Both packages are self-contained, so a separate .NET runtime is not required. Windows SmartScreen may warn for unsigned community builds. Verify the matching SHA-256 file and source before choosing **Run anyway**.

Administrator rights are only needed for system-wide firewall/URL ACL changes. Normal local management can run without elevation.

## Portable layout

```text
HighPop/
├─ HighPop.exe
└─ assets/
   ├─ README.txt
   ├─ presets/       # editable shipped/community presets
   ├─ data/          # settings, profiles, history, users, SteamCMD
   ├─ servers/       # Rust server installations
   ├─ backups/       # full and incremental backups
   └─ logs/          # application diagnostics
```

Secrets are protected at rest with Windows DPAPI. They can only be decrypted by the same Windows user on the same machine. Treat a copied `assets/data` folder as sensitive even though the secret fields are encrypted.

## Rust port model

HighPop reserves and conflict-checks the full set when a profile is created:

| Default | Protocol | Purpose |
|---:|---|---|
| 28015 | UDP | Game traffic |
| 28016 | TCP | WebRCON |
| 28017 | UDP | Steam query |
| 28083 | TCP | Rust+ companion app |

The query port must differ from the game port, and the RCON password must be at least 12 characters. New profiles receive a random 32-character password. See the [official Rust server guide](https://wiki.facepunch.com/rust/Creating-a-server) for current server requirements.

## Stage 2 moderation

The Players workspace supports permanent and native timed bans using Rust's `banid` duration format, including values such as `30m`, `12h`, `7d`, and `1M7d`. Online players can be selected for confirmation-gated bulk kick or ban operations. Staff notes, whitelist state, and HighPop-issued ban metadata are persisted locally in `assets/data/rust_player_moderation.json`.

Rust does not provide a built-in true whitelist. HighPop's **Allow whitelist** and **Remove whitelist** actions use the `whitelist.allow` permission and therefore require Carbon or Oxide/uMod plus the [Whitelist plugin described by Facepunch](https://wiki.facepunch.com/rust/Creating_a_hidden_whitelisted_server). Rust's live bans and mod permissions remain authoritative; HighPop's local records are an operator aid and audit trail.

## Stage 3 Rust operations

HighPop can automatically establish WebRCON after the Rust websocket starts and recover after a dropped connection. This feeds accurate join/leave sessions, moderation, and live player counts without requiring an operator to connect manually after every restart.

The dedicated **Rust** tab separates Facepunch browser tags, community vanilla/modded/development labels, custom variables, and the Rust log directory from general process settings. “Official” is intentionally not offered as a switch: that browser placement is assigned by Facepunch, not by a server launch setting.

## Stage 4 configuration and releases

Rust custom variables are read from and saved to `server/<identity>/cfg/server.cfg`. Facepunch documents this as the startup configuration file for larger variable sets and notes that its values take priority over matching command-line values. HighPop loads active assignments already in that file, preserves comments and unrelated lines, and only rewrites changed or explicitly disabled rows.

Upgrading from v0.3 is automatic: HighPop moves variables from its own managed `serverauto.cfg` block into `server.cfg` the next time the configuration is saved or Rust starts. A value already present in `server.cfg` wins. HighPop removes only its marked legacy block and leaves any other `serverauto.cfg` content alone.

Release tags produce five Windows x64 assets: the direct self-contained `.exe`, its SHA-256 file, the recommended portable ZIP, its SHA-256 file, and a JSON manifest containing sizes, hashes, and the source commit. Authenticode signing is enabled automatically when the repository signing certificate secrets are configured.

The **Automation** tab explains local lifecycle rules and the built-in Discord bot. Scheduled actions for one server are serialized and report their last result; restart operations warn players, save the world, stop cleanly, run configured backups, and start again. The bot keeps live status and private administrator boards updated and sends approved actions back to the local manager.

## Remote access security

The web dashboard and API are disabled by default. Enabling remote access generates a 256-bit token, stores it with DPAPI, and requires it on `/api/**`. Use a trusted LAN, firewall allow-list, VPN, or HTTPS reverse proxy; the embedded listener itself serves HTTP. Public status pages expose only the selected server's basic status, and wake requests are opt-in and rate-limited.

## Build from source

Requirements: Windows 10/Server 2019 or newer and the .NET 10 SDK.

```powershell
git clone https://github.com/RogueAssassin/HighPop-Rust-Manager.git
cd HighPop-Rust-Manager
dotnet restore HighPop.sln
dotnet build HighPop.sln -c Release
dotnet publish HighPop/HighPop.csproj -c Release -r win-x64 --self-contained true -o publish_out
```

Or run `./Build-HighPop.ps1`. Release builds use single-file publishing with native libraries and managed content bundled into `HighPop.exe`; only editable presets and the asset-layout guide ship beside it. The script writes a direct executable, portable ZIP, SHA-256 files, and a JSON manifest under `artifacts/`.

## Architecture

HighPop is a .NET 10 WPF/MVVM application purpose-built for Rust Dedicated Server. Rust is the only server type shipped, loaded, installed, queried, or controlled. Services handle SteamCMD, Rust process supervision, Facepunch WebRCON, Oxide/Carbon, wipes, backups, schedules, notifications, metrics, remote API access, and persistence.

## Project origins

HighPop is derived from the MIT-licensed [Windows Game Server (WGS)](https://github.com/MadBee71/WGS), whose broad process-management foundation made this Rust-focused edition possible. The original copyright and MIT permission notice are retained.

Feature research also considered [Rust Server Manager FMX](https://github.com/AdriaanBoshoff/Rust-Server-Manager-FMX), [Rustadmin](https://www.rustadmin.com/), [MyRustServer](https://myrustserver.com/), [CFTools Cloud](https://cftools.com/title/rust), and [EU Game Host WebRCON](https://rcon.eugamehost.com/). No proprietary source was copied. No GPL-licensed Rust Server Manager FMX code is included.

See [CHANGELOG.md](CHANGELOG.md) for release notes, [NOTICE.md](NOTICE.md) for attribution and third-party notes, [SECURITY.md](SECURITY.md) for vulnerability reporting, and [CONTRIBUTING.md](CONTRIBUTING.md) for development guidance.

## License

MIT. See [LICENSE](LICENSE).
