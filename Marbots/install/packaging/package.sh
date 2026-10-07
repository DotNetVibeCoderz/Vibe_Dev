#!/usr/bin/env bash
# Builds the Marbots release archive for one platform: server (self-contained), `marbots` CLI and `marbots-host`
# (single files), the install scripts and a README.
#   install/packaging/package.sh <version> <rid> <out-dir>
# Output: <out-dir>/marbots-<version>-<rid>.zip (Windows) or .tar.gz, plus <out-dir>/stage-<rid>/ for .deb builds.
set -euo pipefail
VERSION="$1"; RID="$2"; OUT="$(mkdir -p "$3" && cd "$3" && pwd)"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
NAME="marbots-$VERSION-$RID"
STAGE="$OUT/stage-$RID/$NAME"
rm -rf "$OUT/stage-$RID"
mkdir -p "$STAGE/server" "$STAGE/bin"

common=(-c Release -r "$RID" --self-contained -p:Version="$VERSION" -p:DebugType=none -p:GenerateDocumentationFile=false)
dotnet publish "$ROOT/src/Marbots.Server" "${common[@]}" -o "$STAGE/server"
dotnet publish "$ROOT/src/Marbots.Cli" "${common[@]}" -p:PublishSingleFile=true -p:PackAsTool=false -o "$OUT/stage-$RID/cli"
dotnet publish "$ROOT/src/Marbots.AgentHost" "${common[@]}" -p:PublishSingleFile=true -o "$OUT/stage-$RID/host"
ext=""; [[ "$RID" == win-* ]] && ext=".exe"
cp "$OUT/stage-$RID/cli/marbots$ext" "$STAGE/bin/"
cp "$OUT/stage-$RID/host/marbots-host$ext" "$STAGE/bin/"
rm -rf "$OUT/stage-$RID/cli" "$OUT/stage-$RID/host"
# The server downloads host binaries for other platforms from the release on demand (checked against SHA256SUMS).
rm -rf "$STAGE/server/data"

if [[ "$RID" == win-* ]]; then cp "$ROOT/install/install.ps1" "$STAGE/"; else cp "$ROOT/install/install.sh" "$STAGE/"; chmod +x "$STAGE/install.sh" "$STAGE/bin/"* "$STAGE/server/Marbots.Server"; fi
cat > "$STAGE/README.txt" <<EOF
Marbots $VERSION ($RID) — multi-agent collaboration platform
Created by Gravicode Studios, led by Kang Fadhil.

  server/   the Marbots server (web UI + API), started by the installer as a service
  bin/      marbots (CLI) and marbots-host (agent host for other computers)

Install:   $( [[ "$RID" == win-* ]] && echo ".\\install.ps1 -Archive <this zip>   (or run it from the extracted folder)" || echo "./install.sh --archive <this tar.gz>" )
Docs:      https://github.com/DotNetVibeCoderz/Vibe_Dev/blob/main/Marbots/docs/en/installation.md
EOF

cd "$OUT/stage-$RID"
if [[ "$RID" == win-* ]]; then
  rm -f "$OUT/$NAME.zip"
  if command -v zip >/dev/null 2>&1; then zip -qr "$OUT/$NAME.zip" "$NAME"; else
    win() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else echo "$1"; fi; }
    powershell -NoProfile -Command "Compress-Archive -Path '$(win "$PWD/$NAME")' -DestinationPath '$(win "$OUT/$NAME.zip")' -Force"
  fi
else
  tar -czf "$OUT/$NAME.tar.gz" "$NAME"
fi
echo "packaged $OUT/$NAME.$( [[ "$RID" == win-* ]] && echo zip || echo tar.gz )"
