# Linux test plan

HighPop runs on Linux as a desktop app and a headless host (see [the install guide](install.md)). These tests check the Linux groundwork in `HighPop.Core` on a real host, and the installers on a disposable machine.

## Stage 1: self-check (about 5 minutes)

**VM:** Ubuntu Server 24.04 LTS (or Debian 12), x86-64, 1 vCPU, 1 GB RAM, internet access. No .NET install needed.

Build the check on the Windows machine (the repo root):

```powershell
dotnet publish HighPop.LinuxCheck/HighPop.LinuxCheck.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:InvariantGlobalization=true -p:DebugType=none -o artifacts/linuxcheck
tar -czf artifacts/linuxcheck/HighPop.LinuxCheck.tar.gz -C artifacts/linuxcheck HighPop.LinuxCheck
```

Copy it to the VM and run it:

```bash
scp artifacts/linuxcheck/HighPop.LinuxCheck.tar.gz USER@VM:~/
# on the VM
sudo apt update && sudo apt install -y lib32gcc-s1 lib32stdc++6 ca-certificates   # SteamCMD is a 32-bit program
tar xzf HighPop.LinuxCheck.tar.gz && chmod +x HighPop.LinuxCheck
./HighPop.LinuxCheck          # offline checks only
./HighPop.LinuxCheck --all    # adds the SteamCMD and Oxide downloads
```

| Check | What a pass proves |
|---|---|
| Secrets | Saved passwords are encrypted with a random key file that only its owner can read |
| CPU, memory, drives | Values come from `/proc`, CPU rises under load, no virtual mounts listed |
| Network | The process's own listening port and connections are found by PID |
| SteamCMD | Valve's `tar.gz` extracts, `steamcmd.sh` is executable and `+quit` exits 0 |
| Oxide | `Oxide.Rust-linux.zip` is downloaded, checked against GitHub's SHA-256 and installed |

**Send back:** the whole output. A failing check prints its reason; a SteamCMD failure usually means a missing 32-bit library.

## Stage 2: a real Rust server (optional, about an hour)

**VM:** 4 vCPU, 8 GB RAM, 40 GB disk. This checks the launch settings HighPop uses on Linux (library path, executable name).

```bash
mkdir -p ~/steamcmd && cd ~/steamcmd && curl -sSL https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz | tar xz
./steamcmd.sh +force_install_dir ~/rust +login anonymous +app_update 258550 validate +quit
cd ~/rust
export LD_LIBRARY_PATH="$PWD/RustDedicated_Data/Plugins/x86_64:$LD_LIBRARY_PATH"
./RustDedicated -batchmode -nographics -server.port 28015 -rcon.port 28016 -rcon.web 1 -rcon.password changeme \
  -server.identity test -server.worldsize 1000 +server.hostname "HighPop Linux test"
```

**Pass:** the console reaches "Server startup complete". Try it again without the `LD_LIBRARY_PATH` line to see
whether Rust really needs it; that tells us whether HighPop must set it.

## Not covered yet

- Carbon and the RAM cap are listed below. Running HighPop itself on Linux is covered by Stage 3.
- Carbon on Linux (blocked in HighPop until its archive layout and start environment are tested).
- Oxide loading under a running server, and the RAM cap (not enforced on Linux).
## Stage 3: the installers (about 10 minutes, disposable machine only)

`scripts/test-linux-installers.sh <package-dir> --yes` installs and removes every package for real: both `.deb`
files, `install-desktop.sh`, `install-server.sh` (with its systemd service) and the AppImage. It uses `sudo` and
changes the system, so run it on a throwaway VM or a CI runner. CI runs it on every push to `main` and on pull requests.

```bash
# <package-dir> holds the .deb files, the tarballs, the AppImage and their .sha256 files
scripts/test-linux-installers.sh ./package_out --yes
```

**Pass:** it ends with `All installer checks passed.`
