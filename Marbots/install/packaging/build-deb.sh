#!/usr/bin/env bash
# Builds marbots_<version>_<arch>.deb from a package.sh stage (Debian/Ubuntu, systemd).
#   install/packaging/build-deb.sh <version> <linux-x64|linux-arm64> <out-dir>
# Layout: /opt/marbots/{server,bin}, /usr/bin/marbots{,-host,-server}, /lib/systemd/system/marbots.service,
# /etc/marbots/marbots.env (conffile), data in /var/lib/marbots (kept on remove, deleted on purge).
set -euo pipefail
VERSION="$1"; RID="$2"; OUT="$(cd "$3" && pwd)"
case "$RID" in linux-x64) ARCH=amd64 ;; linux-arm64) ARCH=arm64 ;; *) echo "linux-x64 or linux-arm64" >&2; exit 2 ;; esac
SRC="$OUT/stage-$RID/marbots-$VERSION-$RID"
[ -d "$SRC" ] || { echo "Run package.sh $VERSION $RID first" >&2; exit 1; }
ROOT="$(mktemp -d)"
trap 'rm -rf "$ROOT"' EXIT

mkdir -p "$ROOT/opt/marbots" "$ROOT/usr/bin" "$ROOT/lib/systemd/system" "$ROOT/etc/marbots" "$ROOT/DEBIAN"
cp -r "$SRC/server" "$SRC/bin" "$ROOT/opt/marbots/"
chmod 755 "$ROOT/opt/marbots/server/Marbots.Server" "$ROOT/opt/marbots/bin/"*
ln -s /opt/marbots/bin/marbots "$ROOT/usr/bin/marbots"
ln -s /opt/marbots/bin/marbots-host "$ROOT/usr/bin/marbots-host"
ln -s /opt/marbots/server/Marbots.Server "$ROOT/usr/bin/marbots-server"

cat > "$ROOT/etc/marbots/marbots.env" <<'EOF'
# Marbots server settings (environment variables; restart with: sudo systemctl restart marbots)
# Listen on all interfaces instead of localhost (URLS overrides appsettings.json):
#URLS=http://0.0.0.0:5170
#Marbots__ApiKey=change-me
#Marbots__Database__Provider=postgresql
#Marbots__Database__ConnectionString=Host=localhost;Database=marbots;Username=marbots;Password=...
EOF

cat > "$ROOT/lib/systemd/system/marbots.service" <<'EOF'
[Unit]
Description=Marbots multi-agent server
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
User=marbots
Group=marbots
WorkingDirectory=/opt/marbots/server
Environment=URLS=http://127.0.0.1:5170
Environment=Marbots__DataDirectory=/var/lib/marbots
Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false
EnvironmentFile=-/etc/marbots/marbots.env
ExecStart=/opt/marbots/server/Marbots.Server
Restart=on-failure
RestartSec=5
NoNewPrivileges=true
ProtectSystem=full
ReadWritePaths=/var/lib/marbots

[Install]
WantedBy=multi-user.target
EOF

SIZE=$(du -sk "$ROOT" | cut -f1)
cat > "$ROOT/DEBIAN/control" <<EOF
Package: marbots
Version: $VERSION
Section: devel
Priority: optional
Architecture: $ARCH
Maintainer: Gravicode Studios <noreply@gravicode.id>
Installed-Size: $SIZE
Depends: passwd, libc6, libgcc-s1 | libgcc1, libstdc++6, zlib1g, libssl3 | libssl3t64 | libssl1.1, libicu74 | libicu72 | libicu71 | libicu70 | libicu67 | libicu66 | libicu76
Homepage: https://github.com/DotNetVibeCoderz/Vibe_Dev/tree/main/Marbots
Description: Marbots multi-agent collaboration platform
 Boss Man orchestration of durable AI teammates with memory, skills, MCP tools,
 approvals, schedules and agent hosts. Includes the server (systemd service
 "marbots"), the marbots CLI and the marbots-host agent.
 Created by Gravicode Studios, led by Kang Fadhil.
EOF
echo "/etc/marbots/marbots.env" > "$ROOT/DEBIAN/conffiles"

cat > "$ROOT/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
if [ "$1" = configure ]; then
  getent group marbots >/dev/null || groupadd --system marbots
  getent passwd marbots >/dev/null || useradd --system --gid marbots --home-dir /var/lib/marbots --no-create-home --shell /usr/sbin/nologin marbots
  mkdir -p /var/lib/marbots
  chown -R marbots:marbots /var/lib/marbots
  chmod 750 /var/lib/marbots
  if [ -d /run/systemd/system ]; then
    systemctl daemon-reload
    systemctl enable marbots >/dev/null 2>&1 || true
    systemctl restart marbots || true
    echo "Marbots is running: http://localhost:5170 (settings: /etc/marbots/marbots.env)"
  fi
fi
EOF
cat > "$ROOT/DEBIAN/prerm" <<'EOF'
#!/bin/sh
set -e
if [ -d /run/systemd/system ] && [ "$1" = remove ]; then
  systemctl stop marbots || true
  systemctl disable marbots >/dev/null 2>&1 || true
fi
EOF
cat > "$ROOT/DEBIAN/postrm" <<'EOF'
#!/bin/sh
set -e
if [ "$1" = purge ]; then
  rm -rf /var/lib/marbots
  getent passwd marbots >/dev/null && userdel marbots >/dev/null 2>&1 || true
  getent group marbots >/dev/null && groupdel marbots >/dev/null 2>&1 || true
fi
[ -d /run/systemd/system ] && systemctl daemon-reload || true
EOF
chmod 755 "$ROOT/DEBIAN/postinst" "$ROOT/DEBIAN/prerm" "$ROOT/DEBIAN/postrm"

dpkg-deb --root-owner-group --build "$ROOT" "$OUT/marbots_${VERSION}_${ARCH}.deb" >/dev/null
echo "built $OUT/marbots_${VERSION}_${ARCH}.deb"
