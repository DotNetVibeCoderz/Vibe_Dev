# DotCode installer for Windows (PowerShell 5.1+ or PowerShell 7).
#
#   irm https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Dev/main/DotCode/install/install.ps1 | iex
#
# Environment:
#   DOTCODE_VERSION       install a specific version (e.g. 0.2.0); default: latest release
#   DOTCODE_INSTALL_DIR   target directory (default: %LOCALAPPDATA%\Programs\DotCode)
#   DOTCODE_RELEASES_API  / DOTCODE_DOWNLOAD_BASE   mirrors (default: GitHub)
#   DOTCODE_NO_PATH=1     do not add the directory to the user PATH
#
# Downloads the NativeAOT build, verifies it against the release's SHA256SUMS and installs dotcode.exe for the
# current user (no administrator rights). Built by Gravicode Studios, led by Kang Fadhil.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch {}

$repo = if ($env:DOTCODE_REPO) { $env:DOTCODE_REPO } else { 'DotNetVibeCoderz/Vibe_Dev' }
$api = if ($env:DOTCODE_RELEASES_API) { $env:DOTCODE_RELEASES_API.TrimEnd('/') } else { "https://api.github.com/repos/$repo" }
$downloadBase = if ($env:DOTCODE_DOWNLOAD_BASE) { $env:DOTCODE_DOWNLOAD_BASE.TrimEnd('/') } else { "https://github.com/$repo/releases/download" }
$installDir = if ($env:DOTCODE_INSTALL_DIR) { $env:DOTCODE_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\DotCode' }

function Fail([string]$message) { Write-Host "dotcode install: $message" -ForegroundColor Red; throw $message }

$arch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
$rid = switch ($arch) { 'AMD64' { 'win-x64' } 'ARM64' { 'win-arm64' } default { Fail "unsupported architecture: $arch" } }

if ($env:DOTCODE_VERSION) {
    $tag = 'dotcode-v' + $env:DOTCODE_VERSION.TrimStart('v')
} else {
    $releases = Invoke-RestMethod -Uri "$api/releases?per_page=50" -Headers @{ Accept = 'application/vnd.github+json'; 'User-Agent' = 'dotcode-installer' }
    $release = $releases | Where-Object { $_.tag_name -like 'dotcode-v*' -and -not $_.draft -and -not $_.prerelease } | Select-Object -First 1
    if (-not $release) { Fail 'no DotCode release found' }
    $tag = $release.tag_name
}
$version = $tag.Substring('dotcode-v'.Length)
Write-Host "Installing DotCode $version for $rid..."

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('dotcode-install-' + [Guid]::NewGuid().ToString('n').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
try {
    $asset = "dotcode-$rid.zip"
    $archive = Join-Path $tmp $asset
    try { Invoke-WebRequest -Uri "$downloadBase/$tag/$asset" -OutFile $archive -UseBasicParsing }
    catch { Fail "release $tag has no build for $rid ($asset)" }

    $sums = Join-Path $tmp 'SHA256SUMS'
    $haveSums = $true
    try { Invoke-WebRequest -Uri "$downloadBase/$tag/SHA256SUMS" -OutFile $sums -UseBasicParsing } catch { $haveSums = $false }
    if ($haveSums) {
        $line = Get-Content $sums | Where-Object { $_ -match "^\s*([0-9a-fA-F]{64})\s+\*?$([regex]::Escape($asset))\s*$" } | Select-Object -First 1
        if (-not $line) { Fail "SHA256SUMS has no entry for $asset" }
        $expected = ($line -split '\s+')[0].ToLowerInvariant()
        $actual = (Get-FileHash -Algorithm SHA256 -Path $archive).Hash.ToLowerInvariant()
        if ($expected -ne $actual) { Fail "checksum mismatch for $asset (expected $expected, got $actual) - nothing was installed" }
        Write-Host 'Checksum verified (SHA-256).'
    } else {
        Write-Warning "release $tag publishes no SHA256SUMS; skipping verification"
    }

    $extract = Join-Path $tmp 'x'
    Expand-Archive -Path $archive -DestinationPath $extract -Force
    $exe = Get-ChildItem -Path $extract -Recurse -Filter 'dotcode.exe' | Select-Object -First 1
    if (-not $exe) { Fail "$asset does not contain dotcode.exe" }

    New-Item -ItemType Directory -Force -Path $installDir | Out-Null
    $target = Join-Path $installDir 'dotcode.exe'
    # A running dotcode.exe cannot be overwritten, but it can be renamed.
    if (Test-Path $target) {
        $old = "$target.old"
        if (Test-Path $old) { Remove-Item $old -Force -ErrorAction SilentlyContinue }
        Move-Item $target $old -Force
    }
    Copy-Item $exe.FullName $target -Force
    Write-Host "Installed $target"

    if ($env:DOTCODE_NO_PATH -ne '1') {
        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        if (-not (($userPath -split ';') -contains $installDir)) {
            [Environment]::SetEnvironmentVariable('Path', (($userPath.TrimEnd(';') + ';' + $installDir).TrimStart(';')), 'User')
            Write-Host "Added $installDir to your user PATH (open a new terminal to use it)."
        }
        if (-not (($env:Path -split ';') -contains $installDir)) { $env:Path = "$env:Path;$installDir" }
    }
    & $target --version | Select-Object -First 1
    Write-Host ''
    Write-Host 'Get started:  dotcode            (interactive)'
    Write-Host "              dotcode -p `"hi`"    (one-shot)   -  docs: https://github.com/$repo/tree/main/DotCode#readme"
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
