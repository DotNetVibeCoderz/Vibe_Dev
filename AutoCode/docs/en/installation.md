# Installation

> Auto Code — Gravicode Studios, led by Kang Fadhil

## One command

Clone the repository and run the installer for your platform. It checks prerequisites, builds,
installs `autocode` as a .NET global tool, puts it on your PATH, and verifies it runs.

**Windows**

```powershell
git clone https://github.com/gravicode/autocode.git
cd autocode
.\install.ps1
```

**macOS and Linux**

```bash
git clone https://github.com/gravicode/autocode.git
cd autocode
chmod +x install.sh
./install.sh
```

Neither script needs administrator rights or `sudo`: a .NET global tool installs under your own
profile.

Open a new terminal afterwards, so the PATH change takes effect, then:

```bash
autocode --version
```

## Options

| Flag | Effect |
| --- | --- |
| `-Studio` / `--studio` | Also build and install the desktop settings app |
| `-SkipBuild` / `--skip-build` | Reuse the package already in `artifacts/` |
| `-Uninstall` / `--uninstall` | Remove the `autocode` command |
| `-h` / `--help` | Usage (shell script only) |

Re-running the installer upgrades an existing installation in place; it does not need to be
uninstalled first.

## What the installer does

1. Confirms a .NET 10 SDK is present, and stops with an install command if it is not.
2. Builds the solution in Release.
3. Packs `AutoCode.Cli` into `artifacts/`.
4. Installs or updates the `Gravicode.AutoCode` global tool from that package.
5. Adds the .NET tools directory to your **persisted** PATH if it is missing.
6. Runs `autocode --version` to prove the whole path works.

Nothing is written outside your home directory and the repository checkout.

## Prerequisites

Auto Code needs the [.NET 10 SDK](https://dotnet.microsoft.com/download). The runtime alone is not
enough — the installer builds from source.

```powershell
winget install Microsoft.DotNet.SDK.10          # Windows
```

```bash
brew install --cask dotnet-sdk                  # macOS
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0   # Linux
```

Check with `dotnet --list-sdks`. Any 10.x entry is enough.

## Point it at a model

Auto Code ships no model. Give it one of three ways.

**An environment variable** — the conventional vendor variables need no configuration at all:

```bash
export OPENAI_API_KEY=sk-...     # or ANTHROPIC_API_KEY, GEMINI_API_KEY, DEEPSEEK_API_KEY…
autocode
```

**A local runtime** — no key, no network:

```bash
ollama pull qwen2.5-coder:14b
autocode --provider ollama
```

**A settings file** — `autocode config init` scaffolds one, or use Studio below.

Then verify everything, including a live request to the model:

```bash
autocode doctor
```

## Auto Code Studio

A cross-platform desktop app for managing providers, endpoints, models and API keys, so credentials
never have to be edited as JSON by hand.

![Auto Code Studio](../assets/autocode-studio.png)

```powershell
.\install.ps1 -Studio      # Windows — adds a Start-menu entry
```

```bash
./install.sh --studio      # macOS/Linux — adds an `autocode-studio` command
```

What it gives you that hand-editing does not:

- **Verification.** A "Test connection" that sends a real request and reports latency, tokens and
  the model's literal reply. A filled-in form proves nothing; a round trip proves everything.
- **The right file.** Choose between user settings, project settings, and the gitignored local
  settings, with a warning when a literal key is about to land in a file meant to be committed.
- **Environment export.** Copy every profile as shell exports, to keep keys out of files entirely.

Studio edits only the provider section. Permissions, hooks, MCP servers and verify commands in the
same file are left exactly as they were — including profiles Studio itself cannot parse.

## Installing without the scripts

```bash
dotnet build -c Release
dotnet pack src/AutoCode.Cli -c Release -o ./artifacts
dotnet tool install --global --add-source ./artifacts Gravicode.AutoCode
```

To run from the checkout without installing anything:

```bash
dotnet run --project src/AutoCode.Cli -- --help
```

## Upgrading

```bash
git pull
./install.sh          # or .\install.ps1
```

## Uninstalling

```powershell
.\install.ps1 -Uninstall
```

```bash
./install.sh --uninstall
```

Settings and saved sessions are deliberately left behind. Remove them yourself:

```bash
rm -rf ~/.autocode
```

## Troubleshooting

**`autocode` is not recognised after installing.** The PATH change applies to new terminals only.
Open a new one. If it still fails, check that the tools directory is on PATH:

```powershell
[Environment]::GetEnvironmentVariable('PATH','User') -split ';' | Select-String '.dotnet'
```

```bash
echo "$PATH" | tr ':' '\n' | grep dotnet
```

**"Project file does not exist".** Run the installer from the repository root, not from inside
`src/`.

**"Auto Code needs .NET 10".** An older SDK is installed. `dotnet --list-sdks` shows what is there;
installing 10 alongside an older version is fine and does not disturb existing projects.

**The build fails on a fresh clone.** Restore packages explicitly and read the first error rather
than the last: `dotnet restore && dotnet build -c Release`.

**`autocode doctor` fails at the round trip.** The installation is fine; the provider is not. See
[providers](providers.md#troubleshooting).
