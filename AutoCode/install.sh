#!/usr/bin/env bash
#
# Auto Code installer — macOS and Linux
# Gravicode Studios (Kang Fadhil)
#
# Builds Auto Code from this repository and installs it as a .NET global tool, so `autocode`
# works from any terminal.
#
#   ./install.sh                 install or upgrade
#   ./install.sh --uninstall     remove
#   ./install.sh --skip-build    reuse an existing package in ./artifacts
#   ./install.sh --studio        also install the Avalonia settings app
#
# No sudo required: a .NET global tool installs under your home directory.

set -euo pipefail

PACKAGE_ID="Gravicode.AutoCode"
CLI_PROJECT="src/AutoCode.Cli/AutoCode.Cli.csproj"
STUDIO_PROJECT="src/AutoCode.Studio/AutoCode.Studio.csproj"
REQUIRED_MAJOR=10

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ARTIFACTS="$REPO_ROOT/artifacts"
TOOLS_PATH="$HOME/.dotnet/tools"

UNINSTALL=0
SKIP_BUILD=0
WITH_STUDIO=0

if [ -t 1 ]; then
    C_RESET=$'\033[0m'; C_CYAN=$'\033[36m'; C_GREEN=$'\033[32m'
    C_YELLOW=$'\033[33m'; C_RED=$'\033[31m'; C_DIM=$'\033[2m'; C_BOLD=$'\033[1m'
else
    C_RESET=""; C_CYAN=""; C_GREEN=""; C_YELLOW=""; C_RED=""; C_DIM=""; C_BOLD=""
fi

step() { printf '%s==> %s%s\n' "$C_CYAN" "$1" "$C_RESET"; }
ok()   { printf '%s    %s%s\n' "$C_GREEN" "$1" "$C_RESET"; }
warn() { printf '%s    %s%s\n' "$C_YELLOW" "$1" "$C_RESET"; }
fail() { printf '%s    %s%s\n' "$C_RED" "$1" "$C_RESET"; }

for arg in "$@"; do
    case "$arg" in
        --uninstall)  UNINSTALL=1 ;;
        --skip-build) SKIP_BUILD=1 ;;
        --studio)     WITH_STUDIO=1 ;;
        -h|--help)    sed -n '3,14p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *)            fail "Unknown option '$arg'. Try --help."; exit 2 ;;
    esac
done

printf '\n  %sAuto Code%s\n' "$C_BOLD" "$C_RESET"
printf '  %sGravicode Studios · Kang Fadhil%s\n\n' "$C_DIM" "$C_RESET"

# ---------------------------------------------------------------- uninstall

if [ "$UNINSTALL" -eq 1 ]; then
    step "Removing Auto Code"

    if dotnet tool uninstall --global "$PACKAGE_ID" >/dev/null 2>&1; then
        ok "autocode removed."
    else
        warn "autocode was not installed as a global tool."
    fi

    printf '\n  %sYour settings and sessions are untouched. Remove them yourself if you want:%s\n' "$C_DIM" "$C_RESET"
    printf '    rm -rf ~/.autocode\n\n'
    exit 0
fi

# ---------------------------------------------------------------- prerequisites

step "Checking prerequisites"

install_hint() {
    if [ "$(uname -s)" = "Darwin" ]; then
        printf '    brew install --cask dotnet-sdk\n'
    else
        printf '    curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0\n'
    fi
    printf '  %sor download it from https://dotnet.microsoft.com/download%s\n' "$C_DIM" "$C_RESET"
}

if ! command -v dotnet >/dev/null 2>&1; then
    # dotnet-install.sh drops the SDK here without touching PATH, so a previous run may have
    # installed it and simply not be visible yet.
    if [ -x "$HOME/.dotnet/dotnet" ]; then
        export PATH="$HOME/.dotnet:$PATH"
    else
        fail "The .NET SDK is not installed, or is not on PATH."
        printf '\n  %sInstall .NET %s, then run this script again:%s\n' "$C_DIM" "$REQUIRED_MAJOR" "$C_RESET"
        install_hint
        printf '\n'
        exit 1
    fi
fi

# `dotnet --version` reports whatever global.json selects, which may be older than the newest
# installed SDK. The full list is what decides whether we can build.
BEST_MAJOR=0
BEST_VERSION=""

while IFS= read -r line; do
    [ -z "$line" ] && continue
    version="${line%% *}"
    major="${version%%.*}"
    case "$major" in
        ''|*[!0-9]*) continue ;;
    esac
    if [ "$major" -gt "$BEST_MAJOR" ]; then
        BEST_MAJOR="$major"
        BEST_VERSION="$version"
    fi
done < <(dotnet --list-sdks 2>/dev/null | awk '{print $1}')

if [ "$BEST_MAJOR" -eq 0 ]; then
    fail "No .NET SDK could be detected (only a runtime may be installed)."
    install_hint
    exit 1
fi

if [ "$BEST_MAJOR" -lt "$REQUIRED_MAJOR" ]; then
    fail "Auto Code needs .NET $REQUIRED_MAJOR; the newest SDK found is $BEST_VERSION."
    install_hint
    exit 1
fi

ok ".NET SDK $BEST_VERSION"

if [ ! -f "$REPO_ROOT/$CLI_PROJECT" ]; then
    fail "Run this script from the Auto Code repository root ($CLI_PROJECT not found)."
    exit 1
fi

# ---------------------------------------------------------------- build

if [ "$SKIP_BUILD" -eq 0 ]; then
    step "Building"
    # Build the directory rather than a named solution file: .NET 10 emits AutoCode.slnx, older
    # SDKs emit AutoCode.sln, and `dotnet build <dir>` finds whichever is there.
    dotnet build "$REPO_ROOT" -c Release --nologo -v quiet
    ok "Compiled."

    step "Packaging"
    rm -rf "$ARTIFACTS"
    dotnet pack "$REPO_ROOT/$CLI_PROJECT" -c Release -o "$ARTIFACTS" --nologo -v quiet
    ok "Package written to artifacts/"
elif [ ! -d "$ARTIFACTS" ]; then
    fail "--skip-build was given but artifacts/ does not exist. Run without it first."
    exit 1
fi

# ---------------------------------------------------------------- install

step "Installing the autocode command"

# `tool install` fails outright when the tool is already present, so an upgrade has to be an
# explicit update. Probing first keeps the output honest about which one happened.
if dotnet tool list --global 2>/dev/null | grep -qi "^${PACKAGE_ID} "; then
    dotnet tool update --global --add-source "$ARTIFACTS" "$PACKAGE_ID" --no-cache
    ok "autocode updated."
else
    dotnet tool install --global --add-source "$ARTIFACTS" "$PACKAGE_ID" --no-cache
    ok "autocode installed."
fi

# ---------------------------------------------------------------- studio (optional)

if [ "$WITH_STUDIO" -eq 1 ]; then
    step "Installing Auto Code Studio"

    if [ ! -f "$REPO_ROOT/$STUDIO_PROJECT" ]; then
        warn "AutoCode.Studio is not present in this checkout; skipping."
    else
        STUDIO_OUT="$HOME/.autocode/studio"
        if dotnet publish "$REPO_ROOT/$STUDIO_PROJECT" -c Release -o "$STUDIO_OUT" --nologo -v quiet; then
            # A launcher on PATH, so it starts the same way on both platforms.
            mkdir -p "$TOOLS_PATH"
            cat > "$TOOLS_PATH/autocode-studio" <<LAUNCHER
#!/usr/bin/env bash
exec "$STUDIO_OUT/AutoCode.Studio" "\$@"
LAUNCHER
            chmod +x "$TOOLS_PATH/autocode-studio"
            ok "Studio installed. Launch it with: autocode-studio"
        else
            warn "Studio failed to publish; the CLI is still installed."
        fi
    fi
fi

# ---------------------------------------------------------------- PATH

# The shell profile is what decides whether `autocode` resolves in a new terminal. The current
# process may well have the directory already — inherited from a parent shell — and that tells us
# nothing about whether the change survives logout.
case "$(basename "${SHELL:-/bin/bash}")" in
    zsh)  PROFILE="$HOME/.zshrc" ;;
    bash) [ "$(uname -s)" = "Darwin" ] && PROFILE="$HOME/.bash_profile" || PROFILE="$HOME/.bashrc" ;;
    fish) PROFILE="$HOME/.config/fish/config.fish" ;;
    *)    PROFILE="$HOME/.profile" ;;
esac

if [ -f "$PROFILE" ] && grep -qF "$TOOLS_PATH" "$PROFILE"; then
    :
else
    step "Adding the tools directory to PATH"

    LINE="export PATH=\"\$PATH:$TOOLS_PATH\""
    [ "$(basename "${SHELL:-}")" = "fish" ] && LINE="fish_add_path $TOOLS_PATH"

    mkdir -p "$(dirname "$PROFILE")"
    printf '\n# Added by the Auto Code installer\n%s\n' "$LINE" >> "$PROFILE"

    ok "Added to $(basename "$PROFILE")."
    warn "Open a new terminal, or run: source $PROFILE"
fi

# Make it usable in this session too, whatever the profile said.
if ! printf '%s' ":$PATH:" | grep -q ":$TOOLS_PATH:"; then
    export PATH="$PATH:$TOOLS_PATH"
fi

# ---------------------------------------------------------------- verify

step "Verifying"

if ! VERSION="$("$TOOLS_PATH/autocode" --version 2>/dev/null)"; then
    fail "autocode was installed but will not run. Open a new terminal and try: autocode --version"
    exit 1
fi

ok "$VERSION"

printf '\n  %sReady.%s\n\n' "$C_GREEN" "$C_RESET"
printf '  %sPoint it at a model, then start:%s\n\n' "$C_DIM" "$C_RESET"
printf '    export OPENAI_API_KEY="sk-..."   # or ANTHROPIC_API_KEY, GEMINI_API_KEY, DEEPSEEK_API_KEY\n'
printf '    autocode doctor                  # verify the whole path, including a live request\n'
printf '    autocode                         # start a session\n\n'
printf '  %sFully local instead, with no API key:%s\n' "$C_DIM" "$C_RESET"
printf '    ollama pull qwen2.5-coder:14b\n'
printf '    autocode --provider ollama\n\n'

if [ "$WITH_STUDIO" -eq 0 ]; then
    printf '  %sPrefer a GUI for keys and endpoints?  ./install.sh --studio%s\n\n' "$C_DIM" "$C_RESET"
fi
