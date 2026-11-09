# HighPop admin guide

Detailed reference for server operators. For an overview and screenshots, see the [README](../README.md).

- [Console and WebRCON](#webrcon)
- [Rust command reference](#rust-command-reference)
- [Players](#players)
- [server.cfg and map size](#servercfg-and-map-size)
- [RogueRust release channels](#roguerust-release-channels)
- [RogueRust plugin catalog](#roguerust-plugin-catalog)
- [Rogue Gaming endpoints](#rogue-gaming-endpoints)
- [Portable layout](#portable-layout)
- [Recommended production setup](#recommended-production-setup)
- [Custom join domains](custom-domains.md)

## WebRCON

HighPop keeps one interactive WebRCON connection per server workspace and one shared background channel
for scheduler, broadcast, Web API, Discord, and Oxide stop commands. Commands run concurrently and replies
are matched by identifier, so a slow command never blocks the next one. Rust sends no reply to `say`, so
in-game messages complete as soon as they are sent instead of waiting out a timeout. Connecting sends a
read-only `serverinfo` probe so a wrong RCON password shows as a failed connection instead of a socket
that closes silently. WebRCON output that Oxide's live log does not show (for example RogueRust
diagnostics) is written to the Console page.

Rust does not read commands from its standard input: a redirected pipe accepts the line and Rust drops
it. HighPop therefore sends every Rust command over WebRCON, connecting on demand when the console is not
connected yet, and uses the process console only as a last resort, with a warning. Rust gives `say` no
reply, but it broadcasts the chat line to every WebRCON client, so HighPop confirms each message:
**Delivered in game**, or a warning when a plugin blocked it.

The RCON password is sent exactly as typed, because Rust compares the raw request path and never
URL-decodes it. Spaces, double quotes, backticks, `# % / < > ? [ \ ] ^ { | }` and non-ASCII characters cannot be carried in that
path, so HighPop reports such passwords as unusable instead of retrying them. Passwords that HighPop
generates are hexadecimal and always work.

## Rust command reference

The per-server **Reference** page (Console group) loads the current command and ConVar metadata from
[Rogue Rust Reference](https://api.roguegaming.com.au). Search results show execution scope,
descriptions, types, and defaults. **Insert** copies only the selected name into the Console input
for operator review; reference entries are never executed automatically. The compact commands and
ConVars dataset is fetched on demand, shared across server workspaces, and cached for offline use.

## Players

The **Players** page shows live server numbers from `serverinfo` (players, queue, joining, server FPS,
entities, memory, uptime and in-game time). Each player row shows ping, play time, health, IP address,
Steam Family Sharing (the game owner's account, which shares its bans) and Rust's anti-hack violation
level, with buttons for the Steam profile, copying the Steam64 ID, kick, ban and teleport.

- **Player tools**: a give menu (common items by category with search, or any short name, an amount
  and an optional skin ID; to one player or everyone), mute and unmute, team info, unlock or reset
  blueprints, clear inventory, kill, teleport one player to another, moderator add/remove (saved with
  `server.writecfg`), queue skip, saved loadouts, and the mute and ban lists. Rust's reply is shown under
  the target.
- **Chat & reports**: live in-game chat from WebRCON (starting with the last 50 lines from `chat.tail`),
  a message box for all players with delivery confirmation, and F7 player reports as they arrive.

Every player tool targets the Steam64 ID when it is known, quotes names, and validates item names,
amounts and loadout names, so a value cannot inject a second console command. Destructive actions ask
for confirmation first.

## server.cfg and map size

Set the map size with **World size** in Server Settings; HighPop passes it to Rust as
`+server.worldsize`. Leave animal, livestock, sea-life, bird, vehicle, and train `*.population`
convars at their defaults: Rust treats them as densities and multiplies them by the map area and
suitable terrain itself, so the vanilla values already fit every map size. HighPop does not add
population lines to `server.cfg`; any you set yourself are still loaded and editable.

When reading `server.cfg`, a trailing `# comment` or `// comment` is never part of a value, and values
an older tool quoted twice (`"\"1\""`) read as `1`. On save, active lines are written as `name "value"`
and an inline comment is moved to its own line just above.

## RogueRust release channels

Each server has its own RogueRust channel in **Server → Configure → Mods**:

- **Stable** is the default and is recommended for production servers.
- **Testing** follows the current RogueRust development build and is intended for development servers.

HighPop reads the selected public channel manifest, validates that it identifies the expected channel, accepts downloads only from the RogueRust public release repository, verifies the DLL's SHA-256, and installs it transactionally. Oxide/uMod uses `RustDedicated_Data/Managed`; Carbon uses `carbon/extensions`. Existing DLLs receive bounded rollback copies.

The selected manifest is also passed to the Rust process, keeping RogueRust self-update checks on that server's chosen channel. Stop Rust before changing the channel or installing the extension.

## RogueRust plugin catalog

**Server → Configure → Mods → RogueRust plugin catalog** lists every plugin published on
[plugins.roguegaming.com.au](https://plugins.roguegaming.com.au) with its category, current version,
and the version installed on that server (read from the plugin's `[Info]` header). **Install**,
**Update**, and **Reinstall** download the single `.cs` file into the active framework's plugins folder
(`oxide/plugins` or `carbon/plugins`). Oxide/Carbon hot-load it, so a running server needs no restart.

Downloads are accepted only from `https://plugins.roguegaming.com.au/plugins/<Name>.cs`, are size-capped,
must be an Oxide/Carbon plugin whose `[Info]` name matches the file, and are swapped in atomically. The
previous file is kept in `highpop-plugin-backups` (newest 5 per plugin). The catalog's plugins require the
RogueRust extension, which is installed from the same page.

## Rogue Gaming endpoints

HighPop only reads from these public Rogue Gaming hosts, over HTTPS, when you use the matching feature:

| Host | Used for |
|---|---|
| `api.roguegaming.com.au` | Rogue Rust Reference command/ConVar metadata (`data/current/*.json`, `data/metadata.json`) |
| `plugins.roguegaming.com.au` | RogueRust plugin catalog, documentation pages, and `.cs` plugin downloads |

## Portable layout

The release ZIP contains only the application, its assets, and release history:

```text
HPRM/
├─ HighPop.exe
├─ CHANGELOG.md
└─ assets/
   ├─ README.txt
   └─ presets/
```

On first use, HighPop creates runtime data below `assets/` and managed servers below `HPRM/Servers/`:

```text
HPRM/
├─ Servers/          # one independent Rust installation per profile
└─ assets/
   ├─ SteamCMD/      # one shared SteamCMD installation for every server location
   ├─ data/          # settings, profiles, schedules, databases, telemetry
   ├─ backups/       # full and incremental backups
   ├─ logsbackup/    # latest two dated Rust/Oxide/Carbon start archives per server
   └─ logs/          # HighPop diagnostics
```

Before each start or restart, HighPop writes the completed Rust, Oxide, and Carbon logs to a
dated ZIP under `assets/logsbackup/<server>/`. It removes the source logs only after the ZIP is
successfully closed and retains exactly the latest two archives for troubleshooting.

Official presets are embedded in `HighPop.exe` as well as included in the portable ZIP. HighPop
restores missing preset files at startup, updates unchanged built-in presets when their bundled
revision changes, and merges newly introduced keys into edited built-in presets without replacing
operator values. Custom preset files are never changed. Built-in presets a release retires are removed
only while they are still byte-identical to the deployed copy; an edited copy stays as a custom preset.

Keep `HighPop.exe`, `assets`, and `Servers` together when moving or backing up an installation. Secrets are protected with Windows DPAPI for the current Windows user and machine; do not publish `assets/data`.

## Recommended production setup

- Give every profile a unique installation folder and unique Game, Query, WebRCON, and Rust+ ports.
- Leave **Always-on** enabled for production and enable **Auto-start** only when the server should launch with HighPop.
- Use Stable RogueRust on production and Testing only on a separate development profile.
- Configure automatic backups before enabling unattended updates or wipes.
- Use tray/background mode when HighPop must retain live console and process handles.
- Keep the remote API disabled unless it is protected by a trusted LAN, VPN, firewall allow-list, or HTTPS reverse proxy.

