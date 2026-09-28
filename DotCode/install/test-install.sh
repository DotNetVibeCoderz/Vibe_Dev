#!/bin/sh
# Tests install.sh against a local fake release server: checksum-verified install, then a tampered archive that
# must be rejected without touching the installed binary. Needs python3 (or python) for the HTTP server.
set -eu
here=$(cd "$(dirname "$0")" && pwd)
work=$(mktemp -d 2>/dev/null || mktemp -d -t dotcode-test)
server_pid=
cleanup() { if [ -n "$server_pid" ]; then kill "$server_pid" 2>/dev/null || true; wait "$server_pid" 2>/dev/null || true; fi; rm -rf "$work"; }
trap cleanup EXIT INT TERM

py=$(command -v python3 || command -v python || command -v py) || { echo "python is required"; exit 1; }
case "$(uname -s)" in
  Linux) rid=linux ;; Darwin) rid=osx ;; *) rid=win ;;
esac
case "$(uname -m)" in aarch64|arm64) rid="$rid-arm64" ;; *) rid="$rid-x64" ;; esac
exe=dotcode; archive="dotcode-$rid.tar.gz"
case "$rid" in win-*) exe=dotcode.exe; archive="dotcode-$rid.zip" ;; esac

# Fake release: a shell script standing in for the binary.
mkdir -p "$work/site/api" "$work/site/dl/dotcode-v9.9.9" "$work/pkg"
printf '#!/bin/sh\necho "9.9.9 (DotCode)"\n' > "$work/pkg/$exe"
chmod 755 "$work/pkg/$exe"
case "$archive" in
  *.tar.gz) tar -czf "$work/site/dl/dotcode-v9.9.9/$archive" -C "$work/pkg" "$exe" ;;
  *.zip) (cd "$work/pkg" && "$py" -c "import zipfile,sys; z=zipfile.ZipFile(sys.argv[1],'w'); z.write(sys.argv[2]); z.close()" "$work/site/dl/dotcode-v9.9.9/$archive" "$exe") ;;
esac
(cd "$work/site/dl/dotcode-v9.9.9" && { command -v sha256sum >/dev/null && sha256sum "$archive" || shasum -a 256 "$archive"; } > SHA256SUMS)
cat > "$work/site/api/releases" <<'EOF'
[{"tag_name": "otherproject-v1.0.0"}, {"tag_name": "dotcode-v9.9.9", "prerelease": false}, {"tag_name": "dotcode-v0.1.0"}]
EOF

port=$(( 20000 + $$ % 20000 ))
(cd "$work/site" && exec "$py" -m http.server "$port" --bind 127.0.0.1 >/dev/null 2>&1) &
server_pid=$!
i=0; until curl -fs "http://127.0.0.1:$port/api/releases" >/dev/null 2>&1; do i=$((i+1)); [ $i -gt 50 ] && { echo "server did not start"; exit 1; }; sleep 0.2; done

run() {
  DOTCODE_RELEASES_API="http://127.0.0.1:$port/api" DOTCODE_DOWNLOAD_BASE="http://127.0.0.1:$port/dl" \
  DOTCODE_INSTALL_DIR="$work/bin" sh "$here/install.sh"
}

out=$(run 2>&1) || { echo "$out"; echo "FAIL: install failed"; exit 1; }
echo "$out" | grep -q "Installing DotCode 9.9.9" || { echo "$out"; echo "FAIL: wrong version picked"; exit 1; }
echo "$out" | grep -q "Checksum verified" || { echo "$out"; echo "FAIL: checksum not verified"; exit 1; }
[ -x "$work/bin/$exe" ] || { echo "FAIL: $exe not installed"; exit 1; }
echo "ok - verified install"

# Tamper with the archive: the installer must refuse and keep the installed binary.
printf 'tampered' >> "$work/site/dl/dotcode-v9.9.9/$archive"
before=$(cat "$work/bin/$exe")
if out=$(run 2>&1); then echo "$out"; echo "FAIL: tampered archive was installed"; exit 1; fi
echo "$out" | grep -q "checksum mismatch" || { echo "$out"; echo "FAIL: no checksum error"; exit 1; }
[ "$(cat "$work/bin/$exe")" = "$before" ] || { echo "FAIL: installed binary changed"; exit 1; }
echo "ok - tampered archive rejected"
