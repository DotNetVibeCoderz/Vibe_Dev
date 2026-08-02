<#
    Auto Code installer — Windows
    Gravicode Studios (Kang Fadhil)

    Builds Auto Code from this repository and installs it as a .NET global tool, so `autocode`
    works from any terminal.

    Usage:
        .\install.ps1                 # install or upgrade
        .\install.ps1 -Uninstall      # remove
        .\install.ps1 -SkipBuild      # reuse an existing package in ./artifacts
        .\install.ps1 -Studio         # also install the Avalonia settings app

    The script never needs administrator rights: a .NET global tool installs under your profile.
#>

#Requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$Uninstall,
    [switch]$SkipBuild,
    [switch]$Studio
)

$ErrorActionPreference = 'Stop'

$PackageId     = 'Gravicode.AutoCode'
$StudioProject = 'src/AutoCode.Studio/AutoCode.Studio.csproj'
$CliProject    = 'src/AutoCode.Cli/AutoCode.Cli.csproj'
$RequiredSdk   = [Version]'10.0.0'
$RepoRoot      = $PSScriptRoot
$Artifacts     = Join-Path $RepoRoot 'artifacts'
$ToolsPath     = Join-Path $env:USERPROFILE '.dotnet\tools'

function Write-Step  { param($Message) Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Ok    { param($Message) Write-Host "    $Message" -ForegroundColor Green }
function Write-Warn  { param($Message) Write-Host "    $Message" -ForegroundColor Yellow }
function Write-Fail  { param($Message) Write-Host "    $Message" -ForegroundColor Red }

function Get-BestSdkVersion {
    # `dotnet --version` reports the SDK selected by global.json, which may be older than the
    # newest installed. The full list is what actually decides whether we can build.
    try { $lines = & dotnet --list-sdks 2>$null } catch { return $null }
    if (-not $lines) { return $null }

    $versions = foreach ($line in $lines) {
        $token = ($line -split ' ')[0]
        $numeric = ($token -split '-')[0]
        try { [Version]$numeric } catch { }
    }

    if (-not $versions) { return $null }
    return ($versions | Sort-Object -Descending | Select-Object -First 1)
}

Write-Host ''
Write-Host '  Auto Code' -ForegroundColor White
Write-Host '  Gravicode Studios - Kang Fadhil' -ForegroundColor DarkGray
Write-Host ''

# ---------------------------------------------------------------- uninstall

if ($Uninstall) {
    Write-Step 'Removing Auto Code'

    & dotnet tool uninstall --global $PackageId
    if ($LASTEXITCODE -eq 0) { Write-Ok 'autocode removed.' }
    else { Write-Warn 'autocode was not installed as a global tool.' }

    Write-Host ''
    Write-Host '  Your settings and sessions are untouched. Remove them yourself if you want:' -ForegroundColor DarkGray
    Write-Host "    Remove-Item -Recurse '$env:USERPROFILE\.autocode'" -ForegroundColor DarkGray
    Write-Host ''
    exit 0
}

# ---------------------------------------------------------------- prerequisites

Write-Step 'Checking prerequisites'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Fail 'The .NET SDK is not installed, or is not on PATH.'
    Write-Host ''
    Write-Host '  Install .NET 10, then run this script again:' -ForegroundColor DarkGray
    Write-Host '    winget install Microsoft.DotNet.SDK.10' -ForegroundColor White
    Write-Host '  or download it from https://dotnet.microsoft.com/download' -ForegroundColor DarkGray
    Write-Host ''
    exit 1
}

$sdk = Get-BestSdkVersion

if (-not $sdk) {
    Write-Fail 'No .NET SDK could be detected (only a runtime may be installed).'
    Write-Host '    Install the SDK: winget install Microsoft.DotNet.SDK.10' -ForegroundColor DarkGray
    exit 1
}

if ($sdk -lt $RequiredSdk) {
    Write-Fail "Auto Code needs .NET $($RequiredSdk.Major); the newest SDK found is $sdk."
    Write-Host '    Install it: winget install Microsoft.DotNet.SDK.10' -ForegroundColor DarkGray
    exit 1
}

Write-Ok ".NET SDK $sdk"

if (-not (Test-Path (Join-Path $RepoRoot $CliProject))) {
    Write-Fail "Run this script from the Auto Code repository root ($CliProject not found)."
    exit 1
}

# ---------------------------------------------------------------- build

if (-not $SkipBuild) {
    Write-Step 'Building'

    # Build the directory rather than a named solution file: .NET 10 emits AutoCode.slnx, older
    # SDKs emit AutoCode.sln, and `dotnet build <dir>` finds whichever is there.
    & dotnet build $RepoRoot -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { Write-Fail 'Build failed.'; exit 1 }
    Write-Ok 'Compiled.'

    Write-Step 'Packaging'

    if (Test-Path $Artifacts) { Remove-Item $Artifacts -Recurse -Force }

    & dotnet pack (Join-Path $RepoRoot $CliProject) -c Release -o $Artifacts --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { Write-Fail 'Packaging failed.'; exit 1 }
    Write-Ok "Package written to artifacts\"
}
elseif (-not (Test-Path $Artifacts)) {
    Write-Fail '-SkipBuild was given but artifacts\ does not exist. Run without -SkipBuild first.'
    exit 1
}

# ---------------------------------------------------------------- install

Write-Step 'Installing the autocode command'

# Always uninstall then install, rather than `tool update`.
#
# The version here does not change between builds during development, and `dotnet tool update`
# treats same-version as "already up to date" — it prints success and leaves the old binary in
# place. Worse, NuGet keeps an extracted copy under ~/.nuget/packages, so even a fresh install can
# resolve to the previously cached bits. Removing both is the only way a reinstall reliably means
# what it says.
$installed = (& dotnet tool list --global 2>$null) -match "^$PackageId\s"

if ($installed) {
    & dotnet tool uninstall --global $PackageId 2>&1 | Out-Null
}

$cached = Join-Path $env:USERPROFILE ".nuget\packages\$($PackageId.ToLowerInvariant())"
if (Test-Path $cached) {
    Remove-Item $cached -Recurse -Force -ErrorAction SilentlyContinue
    Write-Ok 'Cleared the cached package.'
}

& dotnet tool install --global --add-source $Artifacts $PackageId --no-cache

if ($LASTEXITCODE -ne 0) {
    Write-Fail 'Installation failed.'
    exit 1
}

Write-Ok $(if ($installed) { 'autocode replaced.' } else { 'autocode installed.' })

# ---------------------------------------------------------------- studio (optional)

if ($Studio) {
    Write-Step 'Installing Auto Code Studio'

    if (-not (Test-Path (Join-Path $RepoRoot $StudioProject))) {
        Write-Warn 'AutoCode.Studio is not present in this checkout; skipping.'
    }
    else {
        $studioOut = Join-Path $env:LOCALAPPDATA 'AutoCode\Studio'

        & dotnet publish (Join-Path $RepoRoot $StudioProject) -c Release -o $studioOut --nologo -v quiet
        if ($LASTEXITCODE -ne 0) {
            Write-Warn 'Studio failed to publish; the CLI is still installed.'
        }
        else {
            # A Start-menu shortcut, so it is launchable without remembering the path.
            $shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Auto Code Studio.lnk'
            $shell = New-Object -ComObject WScript.Shell
            $link = $shell.CreateShortcut($shortcut)
            $link.TargetPath = Join-Path $studioOut 'AutoCode.Studio.exe'
            $link.WorkingDirectory = $studioOut
            $link.Description = 'Manage Auto Code providers, models and API keys'
            $link.Save()

            Write-Ok "Studio installed to $studioOut"
            Write-Ok 'Added to the Start menu as "Auto Code Studio".'
        }
    }
}

# ---------------------------------------------------------------- PATH

# The persisted user PATH is what decides whether `autocode` resolves in a new terminal. The
# current process may well have the directory already — inherited from a parent shell — and that
# tells us nothing about whether the change will survive a reboot.
$userPath = [Environment]::GetEnvironmentVariable('PATH', 'User')

if ($userPath -notlike "*$ToolsPath*") {
    Write-Step 'Adding the tools directory to PATH'

    $updated = if ([string]::IsNullOrEmpty($userPath)) { $ToolsPath } else { "$userPath;$ToolsPath" }
    [Environment]::SetEnvironmentVariable('PATH', $updated, 'User')

    Write-Ok "Added $ToolsPath to your user PATH."
    Write-Warn 'Open a new terminal for this to apply everywhere.'
}

# Make it usable in this session too, whatever the persisted value was.
if ($env:PATH -notlike "*$ToolsPath*") {
    $env:PATH = "$env:PATH;$ToolsPath"
}

# ---------------------------------------------------------------- verify

Write-Step 'Verifying'

$exe = Join-Path $ToolsPath 'autocode.exe'
$version = if (Test-Path $exe) { & $exe --version 2>$null } else { & autocode --version 2>$null }

if ($LASTEXITCODE -ne 0 -or -not $version) {
    Write-Fail 'autocode was installed but will not run. Open a new terminal and try `autocode --version`.'
    exit 1
}

Write-Ok $version

Write-Host ''
Write-Host '  Ready.' -ForegroundColor Green
Write-Host ''
Write-Host '  Point it at a model, then start:' -ForegroundColor DarkGray
Write-Host ''
Write-Host '    $env:OPENAI_API_KEY = "sk-..."   # or ANTHROPIC_API_KEY, GEMINI_API_KEY, DEEPSEEK_API_KEY' -ForegroundColor White
Write-Host '    autocode doctor                  # verify the whole path, including a live request' -ForegroundColor White
Write-Host '    autocode                         # start a session' -ForegroundColor White
Write-Host ''
Write-Host '  Fully local instead, with no API key:' -ForegroundColor DarkGray
Write-Host '    ollama pull qwen2.5-coder:14b' -ForegroundColor White
Write-Host '    autocode --provider ollama' -ForegroundColor White
Write-Host ''
if (-not $Studio) {
    Write-Host '  Prefer a GUI for keys and endpoints?  .\install.ps1 -Studio' -ForegroundColor DarkGray
    Write-Host ''
}
