#!/usr/bin/env bash
# Marbots installer for Linux and macOS — Gravicode Studios, led by Kang Fadhil.
#
#   curl -fsSL https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Dev/main/Marbots/install/install.sh | bash
#   curl -fsSL …/install.sh | bash -s -- --component host --server http://192.168.1.10:5170 --token mbe_…
#
# Installs the Marbots server (as a service), the `marbots` CLI and the `marbots-host` agent from a GitHub release
# (checked against SHA256SUMS). Run as a normal user for a per-user install, or with sudo for a system install
# (server as a system service under a dedicated "marbots" user). Run `install.sh --help` for all options.
set -euo pipefail

REPO="DotNetVibeCoderz/Vibe_Dev"
COMPONENT="all"
VERSION="latest"
PREFIX=""
DATA=""
PORT="5170"
LISTEN="127.0.0.1"
SERVICE=1
ARCHIVE=""
HOST_SERVER=""
HOST_TOKEN=""
HOST_NAME=""
SERVER_CA=""
UNINSTALL=0
PURGE=0

usage() {
  cat <<'EOF'
Usage: install.sh [options]

  --component all|server|cli|host   What to install (default: all = server + cli + host binary)
  --version latest|X.Y.Z            Release to install (default: latest)
  --prefix DIR                      Install folder (default: ~/.local/share/marbots/app, or /opt/marbots with sudo)
  --data DIR                        Server data folder (default: ~/.local/share/marbots/data, or /var/lib/marbots)
  --port N                          Server port (default: 5170)
  --public                          Listen on all interfaces (default: localhost only)
  --no-service                      Do not register/start a service
  --archive FILE                    Install from a downloaded marbots-<ver>-<rid>.tar.gz (offline)
  --server URL --token TOKEN        With --component host: enroll this computer as an agent host and start it
  --name NAME                       Host name shown in Marbots (default: hostname)
  --server-ca FILE                  Trust a private CA for the server's TLS certificate (host enrollment)
  --uninstall [--purge]             Remove Marbots (--purge also deletes the data folder)
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --component) COMPONENT="$2"; shift 2 ;;
    --version) VERSION="$2"; shift 2 ;;
    --prefix) PREFIX="$2"; shift 2 ;;
    --data) DATA="$2"; shift 2 ;;
    --port) PORT="$2"; shift 2 ;;
    --public) LISTEN="0.0.0.0"; shift ;;
    --no-service) SERVICE=0; shift ;;
    --archive) ARCHIVE="$2"; shift 2 ;;
    --server) HOST_SERVER="$2"; shift 2 ;;
    --token) HOST_TOKEN="$2"; shift 2 ;;
    --name) HOST_NAME="$2"; shift 2 ;;
    --server-ca) SERVER_CA="$2"; shift 2 ;;
    --uninstall) UNINSTALL=1; shift ;;
    --purge) PURGE=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage; exit 2 ;;
  esac
done

c_ok="\033[32m"; c_dim="\033[2m"; c_err="\033[31m"; c_off="\033[0m"
[ -t 1 ] || { c_ok=""; c_dim=""; c_err=""; c_off=""; }
step() { printf "${c_ok}✓${c_off} %s\n" "$*"; }
note() { printf "${c_dim}  %s${c_off}\n" "$*"; }
die() { printf "${c_err}✗ %s${c_off}\n" "$*" >&2; exit 1; }

OS="$(uname -s)"
case "$OS" in Linux) os=linux ;; Darwin) os=osx ;; *) die "Unsupported OS: $OS (use install.ps1 on Windows)" ;; esac
case "$(uname -m)" in x86_64|amd64) arch=x64 ;; aarch64|arm64) arch=arm64 ;; *) die "Unsupported CPU: $(uname -m)" ;; esac
RID="$os-$arch"

SYSTEM=0
[ "$(id -u)" -eq 0 ] && SYSTEM=1
if [ $SYSTEM -eq 1 ]; then
  PREFIX="${PREFIX:-/opt/marbots}"; DATA="${DATA:-/var/lib/marbots}"; BIN_DIR="/usr/local/bin"
else
  PREFIX="${PREFIX:-$HOME/.local/share/marbots/app}"; DATA="${DATA:-$HOME/.local/share/marbots/data}"; BIN_DIR="$HOME/.local/bin"
fi
LABEL="id.gravicode.marbots"
HOST_LABEL="id.gravicode.marbots-host"

need() { command -v "$1" >/dev/null 2>&1 || die "$1 is required"; }

stop_services() {
  if [ "$os" = linux ] && command -v systemctl >/dev/null 2>&1; then
    if [ $SYSTEM -eq 1 ]; then systemctl stop marbots 2>/dev/null || true; else systemctl --user stop marbots marbots-host 2>/dev/null || true; fi
  elif [ "$os" = osx ]; then
    launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
    launchctl bootout "gui/$(id -u)/$HOST_LABEL" 2>/dev/null || true
    [ $SYSTEM -eq 1 ] && launchctl bootout "system/$LABEL" 2>/dev/null || true
  fi
  pkill -f "$PREFIX/current/server/Marbots.Server" 2>/dev/null || true
}

if [ $UNINSTALL -eq 1 ]; then
  stop_services
  if [ "$os" = linux ]; then
    if [ $SYSTEM -eq 1 ]; then systemctl disable marbots 2>/dev/null || true; rm -f /etc/systemd/system/marbots.service; systemctl daemon-reload 2>/dev/null || true
    else systemctl --user disable marbots marbots-host 2>/dev/null || true; rm -f "$HOME/.config/systemd/user/marbots.service" "$HOME/.config/systemd/user/marbots-host.service"; systemctl --user daemon-reload 2>/dev/null || true; fi
  else
    rm -f "$HOME/Library/LaunchAgents/$LABEL.plist" "$HOME/Library/LaunchAgents/$HOST_LABEL.plist" "/Library/LaunchDaemons/$LABEL.plist" 2>/dev/null || true
  fi
  rm -f "$BIN_DIR/marbots" "$BIN_DIR/marbots-host" "$BIN_DIR/marbots-server"
  rm -rf "$PREFIX"
  step "Removed Marbots from $PREFIX"
  if [ $PURGE -eq 1 ]; then rm -rf "$DATA"; step "Deleted data in $DATA"; else note "Data kept in $DATA (use --purge to delete it)"; fi
  exit 0
fi

need tar
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# ---- get the package
if [ -z "$ARCHIVE" ]; then
  need curl
  if [ "$VERSION" = latest ]; then
    # The repository hosts several projects; pick the newest marbots-v* release.
    VERSION="$(curl -fsSL "https://api.github.com/repos/$REPO/releases?per_page=100" | grep -o '"tag_name": *"marbots-v[0-9][^"]*"' | head -1 | sed 's/.*marbots-v//; s/"//')"
    [ -n "$VERSION" ] || die "Could not find a Marbots release"
  fi
  NAME="marbots-$VERSION-$RID.tar.gz"
  URL="https://github.com/$REPO/releases/download/marbots-v$VERSION"
  note "Downloading $NAME"
  curl -fL --progress-bar -o "$WORK/$NAME" "$URL/$NAME" || die "Download failed: $URL/$NAME"
  curl -fsSL -o "$WORK/SHA256SUMS" "$URL/SHA256SUMS" || die "Could not download SHA256SUMS"
  expected="$(grep " \*\?$NAME\$" "$WORK/SHA256SUMS" | awk '{print $1}')"
  [ -n "$expected" ] || die "$NAME is not listed in SHA256SUMS"
  if command -v sha256sum >/dev/null 2>&1; then actual="$(sha256sum "$WORK/$NAME" | awk '{print $1}')"; else actual="$(shasum -a 256 "$WORK/$NAME" | awk '{print $1}')"; fi
  [ "$expected" = "$actual" ] || die "Checksum mismatch for $NAME"
  step "Downloaded and verified Marbots $VERSION ($RID)"
  ARCHIVE="$WORK/$NAME"
else
  [ -f "$ARCHIVE" ] || die "No such file: $ARCHIVE"
  VERSION="$(basename "$ARCHIVE" | sed -n 's/^marbots-\(.*\)-'"$RID"'\.tar\.gz$/\1/p')"
  VERSION="${VERSION:-local}"
fi

# ---- unpack into versions/<v>, then point "current" at it (upgrades keep the data folder)
stop_services
mkdir -p "$PREFIX/versions" "$BIN_DIR"
rm -rf "$PREFIX/versions/$VERSION"
mkdir -p "$PREFIX/versions/$VERSION"
tar -xzf "$ARCHIVE" -C "$PREFIX/versions/$VERSION" --strip-components=1
ln -sfn "$PREFIX/versions/$VERSION" "$PREFIX/current"
APP="$PREFIX/current"
chmod +x "$APP/server/Marbots.Server" "$APP/bin/marbots" "$APP/bin/marbots-host" 2>/dev/null || true
if [ "$os" = osx ]; then
  xattr -dr com.apple.quarantine "$PREFIX/versions/$VERSION" 2>/dev/null || true
  # Apple silicon only runs signed code; sign the executables ad hoc.
  for f in "$APP/server/Marbots.Server" "$APP/bin/marbots" "$APP/bin/marbots-host"; do codesign --force -s - "$f" >/dev/null 2>&1 || true; done
fi
ln -sfn "$APP/bin/marbots" "$BIN_DIR/marbots"
ln -sfn "$APP/bin/marbots-host" "$BIN_DIR/marbots-host"
ln -sfn "$APP/server/Marbots.Server" "$BIN_DIR/marbots-server"
step "Installed Marbots $VERSION in $PREFIX (commands in $BIN_DIR)"
# Keep only the two newest versions.
ls -1dt "$PREFIX"/versions/* 2>/dev/null | tail -n +3 | xargs rm -rf 2>/dev/null || true
case ":$PATH:" in *":$BIN_DIR:"*) ;; *) note "Add $BIN_DIR to your PATH (e.g. in ~/.profile): export PATH=\"$BIN_DIR:\$PATH\"" ;; esac

# ---- server service
install_server() {
  mkdir -p "$DATA"
  local urls="http://$LISTEN:$PORT"
  local exe="$APP/server/Marbots.Server"
  if [ $SERVICE -eq 0 ]; then
    note "Start the server with: Marbots__DataDirectory=$DATA $exe --urls $urls"
    return
  fi
  if [ "$os" = linux ]; then
    command -v systemctl >/dev/null 2>&1 || { note "No systemd; start with: Marbots__DataDirectory=$DATA $exe --urls $urls"; return; }
    if [ $SYSTEM -eq 1 ]; then
      id marbots >/dev/null 2>&1 || useradd --system --home-dir "$DATA" --no-create-home --shell /usr/sbin/nologin marbots
      chown -R marbots: "$DATA"
      mkdir -p /etc/marbots
      [ -f /etc/marbots/marbots.env ] || printf '# Extra settings, e.g. Marbots__ApiKey=… or Marbots__Database__Provider=postgresql\n' > /etc/marbots/marbots.env
      cat > /etc/systemd/system/marbots.service <<EOF
[Unit]
Description=Marbots multi-agent server
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
User=marbots
WorkingDirectory=$APP/server
ExecStart=$exe --urls $urls
Environment=Marbots__DataDirectory=$DATA
Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false
EnvironmentFile=-/etc/marbots/marbots.env
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF
      systemctl daemon-reload && systemctl enable --now marbots >/dev/null
      step "Server running as system service 'marbots' on $urls (settings: /etc/marbots/marbots.env)"
    else
      mkdir -p "$HOME/.config/systemd/user"
      cat > "$HOME/.config/systemd/user/marbots.service" <<EOF
[Unit]
Description=Marbots multi-agent server

[Service]
Type=notify
WorkingDirectory=$APP/server
ExecStart=$exe --urls $urls
Environment=Marbots__DataDirectory=$DATA
Restart=on-failure
RestartSec=5

[Install]
WantedBy=default.target
EOF
      systemctl --user daemon-reload && systemctl --user enable --now marbots >/dev/null
      loginctl enable-linger "$USER" 2>/dev/null || true
      step "Server running as user service 'marbots' on $urls (systemctl --user status marbots)"
    fi
  else
    local plist dir domain
    if [ $SYSTEM -eq 1 ]; then dir=/Library/LaunchDaemons; domain=system; else dir="$HOME/Library/LaunchAgents"; domain="gui/$(id -u)"; fi
    mkdir -p "$dir" "$DATA"
    plist="$dir/$LABEL.plist"
    cat > "$plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>Label</key><string>$LABEL</string>
  <key>ProgramArguments</key><array><string>$exe</string><string>--urls</string><string>$urls</string></array>
  <key>WorkingDirectory</key><string>$APP/server</string>
  <key>EnvironmentVariables</key><dict><key>Marbots__DataDirectory</key><string>$DATA</string></dict>
  <key>RunAtLoad</key><true/><key>KeepAlive</key><true/>
  <key>StandardOutPath</key><string>$DATA/server.log</string><key>StandardErrorPath</key><string>$DATA/server.log</string>
</dict></plist>
EOF
    launchctl bootout "$domain/$LABEL" 2>/dev/null || true
    launchctl bootstrap "$domain" "$plist" 2>/dev/null || launchctl load -w "$plist"
    step "Server running as launchd $( [ $SYSTEM -eq 1 ] && echo daemon || echo agent ) $LABEL on $urls (log: $DATA/server.log)"
  fi
}

# ---- agent host (always for the current user: it works in that user's session)
install_host() {
  [ -n "$HOST_SERVER" ] && [ -n "$HOST_TOKEN" ] || { note "marbots-host installed; enroll with: marbots-host enroll --server <url> --token <token> && marbots-host run"; return; }
  [ $SYSTEM -eq 1 ] && die "Enroll the host as the user it should run as (without sudo)."
  local args=(enroll --server "$HOST_SERVER" --token "$HOST_TOKEN" --name "${HOST_NAME:-$(hostname)}")
  [ -n "$SERVER_CA" ] && args+=(--server-ca "$SERVER_CA")
  "$APP/bin/marbots-host" "${args[@]}"
  [ $SERVICE -eq 1 ] || { note "Start the host with: marbots-host run"; return; }
  if [ "$os" = linux ] && command -v systemctl >/dev/null 2>&1; then
    mkdir -p "$HOME/.config/systemd/user"
    cat > "$HOME/.config/systemd/user/marbots-host.service" <<EOF
[Unit]
Description=Marbots agent host
After=network-online.target

[Service]
ExecStart=$APP/bin/marbots-host run
Restart=always
RestartSec=5

[Install]
WantedBy=default.target
EOF
    systemctl --user daemon-reload && systemctl --user enable --now marbots-host >/dev/null
    loginctl enable-linger "$USER" 2>/dev/null || true
    step "Agent host running (systemctl --user status marbots-host)"
  elif [ "$os" = osx ]; then
    local plist="$HOME/Library/LaunchAgents/$HOST_LABEL.plist"
    mkdir -p "$HOME/Library/LaunchAgents"
    cat > "$plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>Label</key><string>$HOST_LABEL</string>
  <key>ProgramArguments</key><array><string>$APP/bin/marbots-host</string><string>run</string></array>
  <key>RunAtLoad</key><true/><key>KeepAlive</key><true/>
</dict></plist>
EOF
    launchctl bootout "gui/$(id -u)/$HOST_LABEL" 2>/dev/null || true
    launchctl bootstrap "gui/$(id -u)" "$plist" 2>/dev/null || launchctl load -w "$plist"
    step "Agent host running (launchd agent $HOST_LABEL)"
  else
    nohup "$APP/bin/marbots-host" run >/dev/null 2>&1 &
    step "Agent host started"
  fi
}

case "$COMPONENT" in
  all) install_server ;;
  server) install_server ;;
  cli) ;;
  host) install_host ;;
  *) die "Unknown component: $COMPONENT" ;;
esac

if [ "$COMPONENT" = all ] || [ "$COMPONENT" = server ]; then
  for _ in $(seq 1 30); do curl -fs -o /dev/null "http://127.0.0.1:$PORT/api/v1/system" 2>/dev/null && break; sleep 1; done
  if curl -fs -o /dev/null "http://127.0.0.1:$PORT/api/v1/system" 2>/dev/null; then step "Open http://localhost:$PORT"; else note "The server is starting; open http://localhost:$PORT in a moment."; fi
fi
printf "\n  Marbots — Created by Gravicode Studios, led by Kang Fadhil.\n"
