# Installation

[English](../en/installation.md) · [Bahasa Indonesia](../id/installation.md)

Marbots has three parts. You always need the **server**; the others are optional.

| Part | What it is |
|---|---|
| **Server** (`Marbots.Server`, `marbots-server`) | The web UI, API, bots, memory and approvals. Runs as a service. |
| **CLI** (`marbots`) | The command line for the server: chat, bots, hosts, tenants… |
| **Agent host** (`marbots-host`) | Lets bots work on another computer (see [Computers](computers.md)) |

Every release on GitHub (`marbots-v<version>`) has a package for each platform. Each one is self-contained, so .NET
does not need to be installed, and every file is listed in `SHA256SUMS`.

| Platform | Package | Easiest way |
|---|---|---|
| Windows x64 / ARM64 | `marbots-<v>-win-x64.zip` / `win-arm64.zip` | `install.ps1` or Scoop |
| Linux x64 / ARM64 | `marbots-<v>-linux-x64.tar.gz` / `linux-arm64.tar.gz`, `marbots_<v>_amd64.deb` / `arm64.deb` | `.deb` (Debian/Ubuntu) or `install.sh` |
| macOS Intel / Apple silicon | `marbots-<v>-osx-x64.tar.gz` / `osx-arm64.tar.gz` | `install.sh` |
| Docker (amd64, arm64) | `ghcr.io/dotnetvibecoderz/marbots:<v>` | `docker compose` |

After installing, open **http://localhost:5170**, connect a model in **Settings**, and follow
[Getting started](getting-started.md).

## Windows

PowerShell, as a normal user (the server starts when you log on) or **as Administrator** (the server becomes the
Windows Service "Marbots", with data in `%ProgramData%\Marbots`):

```powershell
irm https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Dev/main/Marbots/install/install.ps1 | iex
```

With options (download the script first, or use the scriptblock form):

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Dev/main/Marbots/install/install.ps1))) -Port 8080 -Public
```

| Option | Meaning |
|---|---|
| `-Component all\|server\|cli\|host` | What to install (default `all`) |
| `-Version 0.3.1` | A specific release (default: latest) |
| `-Port 5170`, `-Public` | Port; listen on all interfaces and open the firewall (admin) |
| `-InstallDir`, `-DataDir` | Folders (default `%LOCALAPPDATA%\Programs\Marbots` / `%LOCALAPPDATA%\Marbots\Server`, or Program Files / ProgramData as admin) |
| `-NoService` | Install only; start the server yourself |
| `-Archive <zip>` | Install from a downloaded package (offline) |
| `-Uninstall [-Purge]` | Remove Marbots (`-Purge` also deletes the data) |

`marbots` and `marbots-host` are added to the PATH. Running the installer again upgrades in place and keeps the data.
The two newest versions stay on disk.

**Scoop**:

```powershell
scoop install https://github.com/DotNetVibeCoderz/Vibe_Dev/releases/latest/download/marbots.json
marbots-server          # starts the server in this console
```

The `releases/latest` link points at the newest release in the repository. If another project published after
Marbots, use the versioned URL `…/releases/download/marbots-v<v>/marbots.json`.

## Linux

**Debian / Ubuntu** (`.deb`, systemd service `marbots`, data in `/var/lib/marbots`):

```bash
curl -fLO https://github.com/DotNetVibeCoderz/Vibe_Dev/releases/download/marbots-v0.3.1/marbots_0.3.1_amd64.deb
sudo apt install ./marbots_0.3.1_amd64.deb
sudo nano /etc/marbots/marbots.env      # optional: listen address, API key, database…
sudo systemctl restart marbots
```

`apt remove marbots` keeps the data; `apt purge marbots` deletes it and the `marbots` user.

**Any distribution** (`install.sh`):

```bash
curl -fsSL https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Dev/main/Marbots/install/install.sh | bash          # for your user
curl -fsSL https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Dev/main/Marbots/install/install.sh | sudo bash     # system-wide
```

- **For your user**: installs into `~/.local/share/marbots`, puts commands in `~/.local/bin`, and creates a systemd
  user service `marbots` (lingering enabled, so it runs without a login).
- **System-wide (`sudo`)**: installs into `/opt/marbots`, puts commands in `/usr/local/bin`, and creates a systemd
  service `marbots` that runs as the `marbots` user, with data in `/var/lib/marbots` and settings in
  `/etc/marbots/marbots.env`.

Options (after `bash -s --`): `--component`, `--version`, `--port`, `--public`, `--prefix`, `--data`,
`--no-service`, `--archive <tar.gz>`, `--uninstall [--purge]`.

## macOS

```bash
curl -fsSL https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Dev/main/Marbots/install/install.sh | bash
```

The script picks the Intel or Apple silicon package and removes the quarantine flag. It signs the executables ad hoc,
which Apple silicon requires. The server runs as the launchd agent `id.gravicode.marbots` (with `sudo`: a
LaunchDaemon) and logs to `<data>/server.log`.

## Docker

```bash
git clone --depth 1 https://github.com/DotNetVibeCoderz/Vibe_Dev && cd Vibe_Dev/Marbots
docker compose -f install/docker/compose.yml up -d                                          # SQLite in a volume
docker compose -f install/docker/compose.yml -f install/docker/compose.postgres.yml up -d   # + PostgreSQL
#   compose.sqlserver.yml (SQL Server) or compose.mysql.yml (MySQL) work the same way
```

- The image is `ghcr.io/dotnetvibecoderz/marbots:<version>` (or `:latest`), built for amd64 and arm64. Compose builds
  it from source if you add `--build`.
- Data lives in the `marbots-data` volume (`/data` in the container); the container listens on 8080, which compose
  maps to `${MARBOTS_PORT:-5170}`.
- Settings go in `install/docker/marbots.env` (copy `marbots.env.example`): API key, providers, multi-tenant mode,
  telemetry, and so on.
- Change the default database passwords (`POSTGRES_PASSWORD`, `MSSQL_SA_PASSWORD`, `MYSQL_PASSWORD`) before exposing
  the server.

Single container:

```bash
docker run -d --name marbots -p 5170:8080 -v marbots-data:/data ghcr.io/dotnetvibecoderz/marbots:latest
```

## CLI only

The CLI is in every package (`bin/marbots`). It is also a .NET tool:

```bash
dotnet tool install -g Marbots.Cli
export MARBOTS_URL=http://server:5170 MARBOTS_API_KEY=…
marbots status
```

## Add another computer as an agent host

From the server's **Computers** page you can install a host over SSH in one step, or create a one-time token and run
the installer on that computer as the user the bots should work as:

```bash
curl -fsSL https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Dev/main/Marbots/install/install.sh | bash -s -- \
  --component host --server http://192.168.1.10:5170 --token mbe_xxx
```

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Dev/main/Marbots/install/install.ps1))) -Component host -Server http://192.168.1.10:5170 -Token mbe_xxx
```

The host enrolls, receives a mutual-TLS client certificate, and starts at logon. On Windows it runs in the desktop
session, so computer-use works; on Linux it is a systemd user service, and on macOS a launchd agent. The server
downloads host binaries for SSH installs and container hosts from its own release automatically, checked against
`SHA256SUMS` (setting `HostPackagesUrl`).

## Upgrade and uninstall

| Installed with | Upgrade | Uninstall |
|---|---|---|
| `install.ps1` | Run it again (`-Version` for a specific release) | `install.ps1 -Uninstall [-Purge]` |
| `install.sh` | Run it again | `install.sh --uninstall [--purge]` |
| `.deb` | `sudo apt install ./marbots_<new>_amd64.deb` | `sudo apt remove marbots` / `purge` |
| Scoop | `scoop update marbots` | `scoop uninstall marbots` |
| Docker | `docker compose pull && docker compose up -d` | `docker compose down` (`-v` deletes the data) |

Data (database, workspaces, skills, secrets, keys) is never touched by an upgrade. Back it up by copying the data
folder while the server is stopped, or with your database's backup tools when you use PostgreSQL, SQL Server or MySQL.

## What was tested

- **Windows 11**: user install from the package, server via the logon task, CLI, upgrade and uninstall (PATH and task
  removed, data kept).
- **Ubuntu 24.04** (container): `install.sh` as root, server, CLI and host; uninstall with purge. The `.deb` was
  built, installed (dependencies resolved), run as the `marbots` user, removed (data kept) and purged.
- **Docker**: image built and run with the PostgreSQL overlay.
- **macOS Intel**: host install over SSH with a launchd agent.

Not tested on real machines: the Windows Service path (needs an elevated install), systemd units, and the Scoop
manifest.

---
*Marbots: Created by Gravicode Studios, led by Kang Fadhil.*
