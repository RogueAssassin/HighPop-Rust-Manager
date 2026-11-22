# Installing HighPop (v1.2.5)

HighPop comes in three variants. They share the same code, so a feature or fix lands in all of them.

| Variant | For | What you get |
|---|---|---|
| [**Windows**](#windows) | A Windows PC or server you sit at | The Avalonia desktop app as one `HighPop.exe` |
| [**Linux Desktop**](#linux-desktop) | A Linux PC with a screen | The same desktop app, as an AppImage, a `.deb` or a script install |
| [**Linux Headless**](#linux-headless) | A Linux server with no screen | `HighPop.Server`, a systemd service you control from the command line and the Web API |

Every download is on the [Releases page](../../../releases/latest). Each file has a `.sha256` file beside it.

**Check a download before you run it.** The checksum catches a damaged or incomplete download.

```powershell
# Windows PowerShell
(Get-FileHash .\HighPop-v1.2.5-win-x64.zip -Algorithm SHA256).Hash.ToLower()
Get-Content .\HighPop-v1.2.5-win-x64.zip.sha256
```

```bash
# Linux
sha256sum -c HighPop-v1.2.5-linux-x64.tar.gz.sha256
```

All three variants are 64-bit (x86-64) and self-contained: you do not install .NET.

---

## Windows

**Needs:** Windows 10 or 11, or Windows Server 2012 to 2025, 64-bit. About 150 MB for HighPop, plus room for Rust (about 15 GB per server).

**Install**

1. Download `HighPop-v1.2.5-win-x64.zip` from Releases.
2. Extract the whole `HPRM` folder somewhere you can write to, such as `C:\HPRM`. Do not use `C:\Program Files`, because HighPop keeps its data next to the program.
3. Run `HPRM\HighPop.exe`. Windows SmartScreen may warn about an unsigned community build: choose **More info**, then **Run anyway**.
4. Select **+ Add Server**, then **Install / Update** to fetch SteamCMD and Rust, then **Start**.

The release also has a bare `HighPop-v1.2.5-win-x64.exe` for people who only want the program. Put it in its own folder: it creates `assets` and `Servers` beside itself.

**What the folder holds**

```text
HPRM/
├─ HighPop.exe     the whole application
├─ CHANGELOG.md
├─ assets/         settings, databases, backups, logs, SteamCMD, presets
└─ Servers/        one folder per Rust server
```

Back up the whole `HPRM` folder. Passwords and tokens in `assets/data` are encrypted for the current Windows user, so a copy opened under another account asks you to re-enter them.

**Updates:** HighPop checks GitHub when it starts and every four hours. Use the update badge in the title bar or the **Support** page. The download is checked against its SHA-256 and swapped in after HighPop exits; your `assets` and `Servers` are never overwritten. Running Rust servers keep running through the update.

**Start with Windows:** turn it on in **Settings**. HighPop creates a Task Scheduler logon task when it runs as administrator. Otherwise (a normal user) it falls back to a per-user entry in `HKCU\...\Run`. Both start HighPop with `--background`, which opens it to the tray.

**Administrator rights** are only needed to add Windows Firewall rules and HTTP URL reservations. Run HighPop as a normal user otherwise.

**Remove:** turn off Start with Windows, close HighPop (Exit from the tray), and delete the `HPRM` folder. Stop your Rust servers first if you do not want them to keep running.

---

## Linux Desktop

**Needs:** a 64-bit Linux desktop with X11 or Wayland (XWayland), Ubuntu 22.04 or newer, Debian 12 or newer, or a similar current distribution (.NET 10 sets the floor). The desktop libraries `libx11-6`, `libice6`, `libsm6` and `libfontconfig1` are installed with the `.deb`; the other installs expect a normal desktop to have them. To run Rust servers you also need the 32-bit libraries SteamCMD uses:

```bash
sudo apt install lib32gcc-s1 lib32stdc++6 ca-certificates
```

The desktop app is the same one you get on Windows, so everything in the [admin guide](admin-guide.md) applies. Choose one of four ways to install it.

### AppImage (one file, no install)

```bash
chmod +x HighPop-v1.2.5-x86_64.AppImage
./HighPop-v1.2.5-x86_64.AppImage
```

- Needs FUSE 2: `sudo apt install libfuse2t64` (Ubuntu 24.04 and newer) or `libfuse2` (older). Without it, run `./HighPop-v1.2.5-x86_64.AppImage --appimage-extract-and-run`.
- An AppImage is read-only. Servers and settings live in `~/.local/share/highpop`, and the app does not update itself. To update, download the new AppImage.
- Remove: delete the file. Delete `~/.local/share/highpop` too if you want your servers and settings gone.

### apt repository (recommended on Ubuntu: updates arrive through Software Updater)

```bash
sudo install -d -m 0755 /usr/share/keyrings
curl -fsSL https://rogueassassin.github.io/HighPop-Rust-Manager/apt/highpop-archive-keyring.gpg | sudo tee /usr/share/keyrings/highpop-archive-keyring.gpg >/dev/null
echo "deb [signed-by=/usr/share/keyrings/highpop-archive-keyring.gpg] https://rogueassassin.github.io/HighPop-Rust-Manager/apt stable main" | sudo tee /etc/apt/sources.list.d/highpop.list
sudo apt update && sudo apt install highpop-desktop
```

- Packages are signed with the HighPop archive key and published by RogueAssassin. apt checks the signature on every update.
- After this, Ubuntu's Software Updater and `sudo apt upgrade` install new HighPop versions; the root permission comes from Ubuntu, not from HighPop.
- Installing a downloaded `.deb` for the first time adds the same repository for you (see below).

### .deb package (system-wide, Debian and Ubuntu)

```bash
sudo apt install ./highpop-desktop_1.2.5_amd64.deb
```

- Installs to `/opt/highpop-desktop`, adds an application-menu entry and a `highpop-desktop` command.
- The program folder is read-only, so servers and settings live in each user's `~/.local/share/highpop`, and the app does not update itself. A fresh install of a downloaded `.deb` also adds the HighPop apt repository (`/etc/apt/sources.list.d/highpop.list`), so later versions arrive through Software Updater and `apt upgrade`; delete that file to opt out. Otherwise install the next `.deb`. Your data is kept.
- Remove: `sudo apt remove highpop-desktop`. Your data stays in `~/.local/share/highpop`.

### install-desktop.sh (one user, no root, self-updating)

```bash
curl -fsSLO https://github.com/RogueAssassin/HighPop-Rust-Manager/releases/latest/download/install-desktop.sh
less install-desktop.sh          # read it first
bash install-desktop.sh
```

- Run it as your normal user, not root. It downloads the release tarball, checks its SHA-256, refuses an archive that escapes the `HPRM/` folder or contains links, then unpacks to `~/.local/share/highpop-desktop` and adds a menu entry and `~/.local/bin/highpop-desktop`.
- Servers and settings live in the same folder, so it is portable. The app **can update itself** (a banner offers *Install update*).
- Options: `--version vX.Y.Z`, `--tarball FILE` (offline install; the `.sha256` must sit beside it), `--prefix DIR`, `--uninstall` (removes the program, keeps `assets` and `Servers`).

### Tarball (manual, portable)

```bash
tar -xzf HighPop-v1.2.5-linux-x64.tar.gz
./HPRM/HighPop.Desktop
```

Put the `HPRM` folder anywhere you can write to. It behaves like the Windows portable folder and can update itself.

### Where things are

| | AppImage / `.deb` | `install-desktop.sh` / tarball |
|---|---|---|
| Program | read-only | your folder |
| Servers, settings, presets | `~/.local/share/highpop` (or `$HIGHPOP_HOME`) | beside the program |
| Saved passwords | AES-256-GCM, key in `~/.config/highpop/secret.key` (owner-only) | same |
| In-app update | no (the apt repository updates the `.deb`; for an AppImage, install the new file) | yes |

HighPop uses `$HIGHPOP_HOME` when set. Otherwise it keeps data beside the program, falling back to `~/.local/share/highpop` when that folder is not writable.

**Start in the tray:** run `highpop-desktop --background`. A desktop with no system tray (stock GNOME) shows no tray icon; add a tray extension, or keep the window open.

**Firewall:** HighPop does not change your Linux firewall. Open the ports yourself, for example with `ufw`:

```bash
sudo ufw allow 28015/udp    # game
sudo ufw allow 28016/tcp    # rcon (only if you need it from another machine)
```

---

## Linux Headless

**Needs:** a 64-bit Linux server with systemd (Ubuntu 22.04 or newer, Debian 12 or newer, or similar). No desktop libraries. For Rust: `sudo apt install lib32gcc-s1 lib32stdc++6 ca-certificates`. `libicu` is recommended for full globalisation.

The headless host (`HighPop.Server`) runs without a window as a systemd service. It reattaches to running Rust servers after a restart, resumes crash recovery, starts profiles marked auto-start, serves the Web API and exits cleanly on stop. **Rust servers keep running if the service stops.**

### .deb package (recommended on Debian and Ubuntu)

```bash
sudo apt install ./highpop-server_1.2.5_amd64.deb
```

- Installs to `/opt/highpop-server`, creates the system user `highpop`, and enables `highpop-server.service`. A fresh install is **not started** until you add a server.
- Data lives in `/var/lib/highpop` (owned by `highpop`, mode 750). Upgrades restart a running service and keep your data.
- The `highpop-server` command runs the CLI as the service user, so you never need `su`.
- Remove: `sudo apt remove highpop-server`. `/var/lib/highpop` and the `highpop` user stay until you delete them.

### install-server.sh (any systemd distribution)

```bash
curl -fsSLO https://github.com/RogueAssassin/HighPop-Rust-Manager/releases/latest/download/install-server.sh
less install-server.sh           # read it first
sudo bash install-server.sh
```

- Needs root. It downloads the server tarball, checks its SHA-256, refuses an archive that escapes `HPRM/` or contains links, installs to `/opt/highpop-server` (replaced on upgrade), writes `/etc/systemd/system/highpop-server.service`, and adds `/usr/local/bin/highpop-server`.
- The service runs as an unprivileged user (default `highpop`, created if missing). Data lives in `/var/lib/highpop` and is never overwritten or removed by upgrades or `--uninstall`.
- Options: `--version vX.Y.Z`, `--tarball FILE` (offline), `--user NAME`, `--no-start`, `--uninstall`.
- Upgrade: run it again. A running service is stopped and started again; Rust servers are not touched.

### Tarball (manual)

Use `HighPop-v1.2.5-linux-server-x64.tar.gz` and the sample unit `scripts/highpop-server.service` from the source tree. Set `User=`, `WorkingDirectory=` and `ExecStart=` to match where you unpacked it.

### First server

```bash
highpop-server add "My Rust" --autostart      # creates a profile
highpop-server install "My Rust"              # SteamCMD downloads Rust (about 15 GB)
sudo systemctl start highpop-server           # start the host (it starts auto-start servers)
highpop-server list                           # show profiles
highpop-server restart "My Rust"              # also: start, stop
highpop-server setup --port 8080              # enable the Web API and create a token
journalctl -u highpop-server -f               # follow the host log
```

`highpop-server help` lists every command. Use the Web API (see the [admin guide](admin-guide.md#web-api-and-remote-machines)) or a Windows or Linux HighPop on your own PC (**Remote Machines**) to manage a headless host with a window.

**Firewall:** open your game, query and RCON ports yourself. Keep the Web API on a trusted network, a VPN or an HTTPS reverse proxy: it is plain HTTP.

**Back up** `/var/lib/highpop`. Saved passwords there are encrypted with a key in the service user's `~/.config/highpop/secret.key`, so back up both together.

---

## Which one should I pick?

| You have | Use |
|---|---|
| A Windows PC or server | Windows |
| A Linux PC and want a window | Linux Desktop: `.deb` for a shared machine, AppImage for a quick try, `install-desktop.sh` if you want in-app updates |
| A Linux server with no screen | Linux Headless |
| A mix | Run Headless on the servers and Remote Machines from the desktop app on your PC |

## Troubleshooting

| Symptom | Fix |
|---|---|
| AppImage says it needs FUSE | Install `libfuse2t64`, or run it with `--appimage-extract-and-run` |
| Rust or SteamCMD will not start on Linux | Install `lib32gcc-s1 lib32stdc++6` |
| Checksum verification failed | Download again; do not run the file |
| Windows: "Access is denied" when starting with Windows | Update to v1.2.2 or newer: normal users now use the per-user Run entry |
| Linux Desktop: no tray icon | Your desktop has no system tray (stock GNOME); install a tray extension |
| Headless service exits at once | `journalctl -u highpop-server -n 50`; the user needs write access to `/var/lib/highpop` |
| Update banner missing | AppImage and `.deb` installs do not self-update; install the new file |
