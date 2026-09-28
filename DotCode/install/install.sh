#!/bin/sh
# DotCode installer for Linux and macOS (also works from Git Bash / MSYS2 on Windows).
#
#   curl -fsSL https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Dev/main/DotCode/install/install.sh | sh
#
# Environment:
#   DOTCODE_VERSION       install a specific version (e.g. 0.2.0); default: latest release
#   DOTCODE_INSTALL_DIR   target directory (default: ~/.local/bin)
#   DOTCODE_RELEASES_API  / DOTCODE_DOWNLOAD_BASE   mirrors (default: GitHub)
#
# Downloads the NativeAOT build for this platform, verifies it against the release's SHA256SUMS and installs the
# single `dotcode` executable. No root required. Built by Gravicode Studios, led by Kang Fadhil.
set -eu

REPO="${DOTCODE_REPO:-DotNetVibeCoderz/Vibe_Dev}"
API="${DOTCODE_RELEASES_API:-https://api.github.com/repos/$REPO}"
DOWNLOAD_BASE="${DOTCODE_DOWNLOAD_BASE:-https://github.com/$REPO/releases/download}"
VERSION="${DOTCODE_VERSION:-}"
INSTALL_DIR="${DOTCODE_INSTALL_DIR:-$HOME/.local/bin}"

say() { printf '%s\n' "$*"; }
fail() { printf 'dotcode install: %s\n' "$*" >&2; exit 1; }

fetch() { # fetch URL OUTPUT  (returns non-zero on HTTP errors)
  if command -v curl >/dev/null 2>&1; then curl -fsSL --retry 3 -o "$2" "$1"
  elif command -v wget >/dev/null 2>&1; then wget -q -O "$2" "$1"
  else fail "curl or wget is required"; fi
}

# ---- platform
os=$(uname -s)
case "$os" in
  Linux) os=linux ;;
  Darwin) os=osx ;;
  MINGW*|MSYS*|CYGWIN*) os=win ;;
  *) fail "unsupported OS: $os (download a build from https://github.com/$REPO/releases)" ;;
esac
arch=$(uname -m)
case "$arch" in
  x86_64|amd64) arch=x64 ;;
  aarch64|arm64) arch=arm64 ;;
  *) fail "unsupported architecture: $arch" ;;
esac
# Rosetta: prefer the native arm64 build on Apple silicon.
if [ "$os" = osx ] && [ "$arch" = x64 ] && [ "$(sysctl -n sysctl.proc_translated 2>/dev/null || echo 0)" = 1 ]; then arch=arm64; fi
if [ "$os" = linux ] && ls /lib/ld-musl-* >/dev/null 2>&1; then
  fail "musl-based Linux (e.g. Alpine) is not supported by the prebuilt binaries; install the .NET tool instead: dotnet tool install -g DotCode.Cli"
fi
rid="$os-$arch"
exe=dotcode; [ "$os" = win ] && exe=dotcode.exe

tmp=$(mktemp -d 2>/dev/null || mktemp -d -t dotcode)
trap 'rm -rf "$tmp"' EXIT INT TERM

# ---- release
if [ -n "$VERSION" ]; then
  tag="dotcode-v${VERSION#v}"
else
  fetch "$API/releases?per_page=50" "$tmp/releases.json" || fail "could not query $API/releases"
  # First stable DotCode release (the repository also publishes other projects' tags).
  tag=$(grep -o '"tag_name": *"dotcode-v[0-9][0-9.]*"' "$tmp/releases.json" | head -n 1 | sed 's/.*"\(dotcode-v[^"]*\)"/\1/')
  [ -n "$tag" ] || fail "no DotCode release found"
fi
version=${tag#dotcode-v}
say "Installing DotCode $version for $rid..."

# ---- download (tar.gz for Linux/macOS, zip for Windows; older releases only had zips)
if [ "$os" = win ]; then candidates="dotcode-$rid.zip"; else candidates="dotcode-$rid.tar.gz dotcode-$rid.zip"; fi
asset=
for name in $candidates; do
  if fetch "$DOWNLOAD_BASE/$tag/$name" "$tmp/$name" 2>/dev/null; then asset=$name; break; fi
done
[ -n "$asset" ] || fail "release $tag has no build for $rid"

# ---- verify
if fetch "$DOWNLOAD_BASE/$tag/SHA256SUMS" "$tmp/SHA256SUMS" 2>/dev/null; then
  expected=$(grep " \*\{0,1\}$asset\$" "$tmp/SHA256SUMS" | awk '{print $1}' | head -n 1)
  [ -n "$expected" ] || fail "SHA256SUMS has no entry for $asset"
  if command -v sha256sum >/dev/null 2>&1; then actual=$(sha256sum "$tmp/$asset" | awk '{print $1}')
  elif command -v shasum >/dev/null 2>&1; then actual=$(shasum -a 256 "$tmp/$asset" | awk '{print $1}')
  else fail "sha256sum or shasum is required to verify the download"; fi
  [ "$expected" = "$actual" ] || fail "checksum mismatch for $asset (expected $expected, got $actual) - nothing was installed"
  say "Checksum verified (SHA-256)."
else
  say "warning: release $tag publishes no SHA256SUMS; skipping verification" >&2
fi

# ---- extract and install
mkdir -p "$tmp/x"
case "$asset" in
  *.tar.gz) tar -xzf "$tmp/$asset" -C "$tmp/x" ;;
  *.zip)
    if command -v unzip >/dev/null 2>&1; then unzip -q "$tmp/$asset" -d "$tmp/x"
    elif command -v bsdtar >/dev/null 2>&1; then bsdtar -xf "$tmp/$asset" -C "$tmp/x"
    else fail "unzip is required"; fi ;;
esac
src=$(find "$tmp/x" -type f -name "$exe" | head -n 1)
[ -n "$src" ] || fail "$asset does not contain $exe"
mkdir -p "$INSTALL_DIR"
if [ -f "$INSTALL_DIR/$exe" ] && [ "$os" = win ]; then mv -f "$INSTALL_DIR/$exe" "$INSTALL_DIR/$exe.old" 2>/dev/null || true; fi
cp "$src" "$INSTALL_DIR/$exe.new"
chmod 755 "$INSTALL_DIR/$exe.new"
mv -f "$INSTALL_DIR/$exe.new" "$INSTALL_DIR/$exe"
if [ "$os" = osx ]; then xattr -d com.apple.quarantine "$INSTALL_DIR/$exe" 2>/dev/null || true; fi

say "Installed $INSTALL_DIR/$exe"
"$INSTALL_DIR/$exe" --version 2>/dev/null | head -n 1 || true
case ":$PATH:" in
  *":$INSTALL_DIR:"*) ;;
  *) say ""; say "Add $INSTALL_DIR to your PATH, e.g.:"; say "  echo 'export PATH=\"$INSTALL_DIR:\$PATH\"' >> ~/.profile && . ~/.profile" ;;
esac
say ""
say "Get started:  dotcode            (interactive)"
say "              dotcode -p \"hi\"    (one-shot)     -  docs: https://github.com/$REPO/tree/main/DotCode#readme"
