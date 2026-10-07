<#
.SYNOPSIS
  Marbots installer for Windows - Gravicode Studios, led by Kang Fadhil.

.DESCRIPTION
  Installs the Marbots server, the `marbots` CLI and the `marbots-host` agent from a GitHub release (checked against
  SHA256SUMS). From an elevated PowerShell the server becomes the Windows Service "Marbots" (data in
  %ProgramData%\Marbots); otherwise it is installed for the current user and starts at logon.

  irm https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Dev/main/Marbots/install/install.ps1 | iex
  & ([scriptblock]::Create((irm .../install.ps1))) -Component host -Server http://192.168.1.10:5170 -Token mbe_...

.EXAMPLE
  .\install.ps1                                   # server + CLI + host binary, latest release
.EXAMPLE
  .\install.ps1 -Component host -Server http://192.168.1.10:5170 -Token mbe_xxx   # join this PC as an agent host
.EXAMPLE
  .\install.ps1 -Uninstall -Purge
#>
[CmdletBinding()]
param(
    [ValidateSet('all', 'server', 'cli', 'host')] [string]$Component = 'all',
    [string]$Version = 'latest',
    [string]$InstallDir,
    [string]$DataDir,
    [int]$Port = 5170,
    [switch]$Public,
    [switch]$NoService,
    [string]$Archive,
    [string]$Server,
    [string]$Token,
    [string]$Name = $env:COMPUTERNAME,
    [string]$ServerCa,
    [switch]$Uninstall,
    [switch]$Purge
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$Repo = 'DotNetVibeCoderz/Vibe_Dev'
function Step($m) { Write-Host "  + $m" -ForegroundColor Green }
function Note($m) { Write-Host "    $m" -ForegroundColor DarkGray }
# Native commands run through cmd so their stderr does not become a PowerShell error under -ErrorAction Stop.
function Quiet([string]$commandLine) { cmd /c "$commandLine >nul 2>&1"; $global:LASTEXITCODE = $LASTEXITCODE }
function Fail($m) { Write-Host "  x $m" -ForegroundColor Red; exit 1 }

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$rid = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
if (-not $InstallDir) { $InstallDir = if ($admin) { Join-Path $env:ProgramFiles 'Marbots' } else { Join-Path $env:LOCALAPPDATA 'Programs\Marbots' } }
if (-not $DataDir) { $DataDir = if ($admin) { Join-Path $env:ProgramData 'Marbots' } else { Join-Path $env:LOCALAPPDATA 'Marbots\Server' } }
$binDir = Join-Path $InstallDir 'current\bin'
$pathScope = if ($admin) { 'Machine' } else { 'User' }
$serverTask = 'Marbots Server'
$hostTask = 'Marbots Host'

function Stop-Marbots {
    if (Get-Service -Name Marbots -ErrorAction SilentlyContinue) { Stop-Service Marbots -Force -ErrorAction SilentlyContinue }
    Quiet "schtasks /End /TN `"$serverTask`""
    Get-Process Marbots.Server -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$InstallDir*" } | Stop-Process -Force -ErrorAction SilentlyContinue
}

function Set-PathEntry([string]$dir, [bool]$add) {
    $current = [Environment]::GetEnvironmentVariable('Path', $pathScope)
    $parts = @($current -split ';' | Where-Object { $_ -and $_ -ne $dir })
    if ($add) { $parts += $dir }
    [Environment]::SetEnvironmentVariable('Path', ($parts -join ';'), $pathScope)
}

if ($Uninstall) {
    Stop-Marbots
    if (Get-Service -Name Marbots -ErrorAction SilentlyContinue) { Quiet "sc.exe delete Marbots" }
    Quiet "schtasks /Delete /TN `"$serverTask`" /F"
    Set-PathEntry $binDir $false
    Remove-Item -Recurse -Force $InstallDir -ErrorAction SilentlyContinue
    Step "Removed Marbots from $InstallDir"
    if ($Purge) { Remove-Item -Recurse -Force $DataDir -ErrorAction SilentlyContinue; Step "Deleted data in $DataDir" }
    else { Note "Data kept in $DataDir (use -Purge to delete it). The agent host task 'Marbots Host' is left as is." }
    exit 0
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("marbots-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $work | Out-Null
try {
    # ---- get the package
    if (-not $Archive) {
        if ($Version -eq 'latest') {
            # The repository hosts several projects; pick the newest marbots-v* release.
            $releases = Invoke-RestMethod "https://api.github.com/repos/$Repo/releases?per_page=100" -Headers @{ 'User-Agent' = 'marbots-installer' }
            $tag = ($releases | Where-Object { $_.tag_name -like 'marbots-v*' } | Select-Object -First 1).tag_name
            if (-not $tag) { Fail 'Could not find a Marbots release' }
            $Version = $tag.Substring('marbots-v'.Length)
        }
        $file = "marbots-$Version-$rid.zip"
        $base = "https://github.com/$Repo/releases/download/marbots-v$Version"
        Note "Downloading $file"
        Invoke-WebRequest "$base/$file" -OutFile (Join-Path $work $file) -UseBasicParsing
        Invoke-WebRequest "$base/SHA256SUMS" -OutFile (Join-Path $work 'SHA256SUMS') -UseBasicParsing
        $line = Get-Content (Join-Path $work 'SHA256SUMS') | Where-Object { $_ -match "\s\*?$([regex]::Escape($file))$" } | Select-Object -First 1
        if (-not $line) { Fail "$file is not listed in SHA256SUMS" }
        $expected = ($line -split '\s+')[0]
        $actual = (Get-FileHash (Join-Path $work $file) -Algorithm SHA256).Hash
        if ($actual -ne $expected.ToUpperInvariant()) { Fail "Checksum mismatch for $file" }
        Step "Downloaded and verified Marbots $Version ($rid)"
        $Archive = Join-Path $work $file
    } else {
        if (-not (Test-Path $Archive)) { Fail "No such file: $Archive" }
        if ((Split-Path $Archive -Leaf) -match "^marbots-(.+)-$rid\.zip$") { $Version = $Matches[1] } else { $Version = 'local' }
    }

    # ---- unpack into versions\<v> and point "current" at it (upgrades keep the data folder)
    Stop-Marbots
    $target = Join-Path $InstallDir "versions\$Version"
    $current = Join-Path $InstallDir 'current'
    Remove-Item -Recurse -Force $target -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $target | Out-Null
    $unpack = Join-Path $work 'unpack'
    Expand-Archive $Archive -DestinationPath $unpack -Force
    $inner = Get-ChildItem $unpack -Directory | Select-Object -First 1
    Copy-Item -Recurse -Force (Join-Path $inner.FullName '*') $target
    if (Test-Path $current) { Quiet "rmdir `"$current`"" }
    Quiet "mklink /J `"$current`" `"$target`""
    Get-ChildItem (Join-Path $InstallDir 'versions') -Directory | Sort-Object LastWriteTime -Descending | Select-Object -Skip 2 | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Set-PathEntry $binDir $true
    $env:Path = "$binDir;$env:Path"
    Step "Installed Marbots $Version in $InstallDir (marbots and marbots-host are on the PATH)"

    $exe = Join-Path $current 'server\Marbots.Server.exe'
    $listen = if ($Public) { '0.0.0.0' } else { 'localhost' }
    $urls = "http://${listen}:$Port"

    # ---- server
    if ($Component -in 'all', 'server') {
        New-Item -ItemType Directory -Force $DataDir | Out-Null
        $serverArgs = "--urls $urls --Marbots:DataDirectory `"$DataDir`""
        if ($NoService) {
            Note "Start the server with: & '$exe' $serverArgs"
        } elseif ($admin) {
            if (Get-Service -Name Marbots -ErrorAction SilentlyContinue) { Quiet "sc.exe delete Marbots"; Start-Sleep 2 }
            New-Service -Name Marbots -DisplayName 'Marbots' -Description 'Marbots multi-agent server (Gravicode Studios)' `
                -BinaryPathName "`"$exe`" $serverArgs" -StartupType Automatic | Out-Null
            Quiet "sc.exe failure Marbots reset= 86400 actions= restart/5000/restart/5000/restart/30000"
            Start-Service Marbots
            if ($Public) { New-NetFirewallRule -DisplayName 'Marbots' -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow -ErrorAction SilentlyContinue | Out-Null }
            Step "Server running as Windows Service 'Marbots' on $urls (data: $DataDir)"
        } else {
            $action = New-ScheduledTaskAction -Execute $exe -Argument $serverArgs -WorkingDirectory (Split-Path $exe)
            $trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
            $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
            Register-ScheduledTask -TaskName $serverTask -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null
            Start-ScheduledTask -TaskName $serverTask
            Step "Server starts at logon for $env:USERNAME (task '$serverTask') on $urls"
        }
        if (-not $NoService) {
            $ok = $false
            foreach ($i in 1..40) { try { Invoke-WebRequest "http://localhost:$Port/api/v1/system" -UseBasicParsing -TimeoutSec 2 | Out-Null; $ok = $true; break } catch { Start-Sleep 1 } }
            if ($ok) { Step "Open http://localhost:$Port" } else { Note "The server is starting; open http://localhost:$Port in a moment." }
        }
    }

    # ---- agent host (runs in the user's desktop session so computer-use tools work)
    if ($Component -eq 'host') {
        if (-not ($Server -and $Token)) {
            Note 'marbots-host installed; enroll with: marbots-host enroll --server <url> --token <token>, then marbots-host run'
        } else {
            $hostDir = Join-Path $env:LOCALAPPDATA 'Marbots\Host'
            New-Item -ItemType Directory -Force $hostDir | Out-Null
            Quiet "schtasks /End /TN `"$hostTask`""
            Get-Process marbots-host -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
            $hostExe = Join-Path $hostDir 'marbots-host.exe'
            Copy-Item -Force (Join-Path $current 'bin\marbots-host.exe') $hostExe
            $enroll = @('enroll', '--server', $Server, '--token', $Token, '--name', $Name)
            if ($ServerCa) { $enroll += @('--server-ca', $ServerCa) }
            & $hostExe @enroll
            if ($LASTEXITCODE -ne 0) { Fail 'Enrollment failed' }
            if (-not $NoService) {
                Quiet "schtasks /Create /TN `"$hostTask`" /TR `"\`"$hostExe\`" run`" /SC ONLOGON /RL LIMITED /IT /F"
                Quiet "schtasks /Run /TN `"$hostTask`""
                Step "Agent host runs at logon in your desktop session (task '$hostTask')"
            }
        }
    }
    Write-Host "`n  Marbots - Created by Gravicode Studios, led by Kang Fadhil.`n"
} finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
