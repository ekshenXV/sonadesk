[CmdletBinding()]
param(
    [string]$Zig,
    [string]$SdkHeaders,
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'SonaDesk')
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Build-Downloads.ps1')
$desktop = Join-Path $PSScriptRoot 'Desktop'
$stage = Join-Path $PSScriptRoot 'obj\desktop'
$native = Join-Path $stage 'native'
New-Item -ItemType Directory -Path $stage, $native -Force | Out-Null
Copy-Item -LiteralPath "$desktop\package.json","$desktop\package-lock.json" -Destination $stage -Force

& npm.cmd --prefix $stage ci --no-fund --no-audit
if ($LASTEXITCODE -ne 0) { throw 'Desktop dependencies could not be installed.' }

# Use the Windows archive tool to build without Electron's native ZIP addon.
$electron = Join-Path $stage 'node_modules\electron'
$electronVersion = (Get-Content -LiteralPath (Join-Path $electron 'package.json') -Raw | ConvertFrom-Json).version
$electronArchive = "electron-v$electronVersion-win32-x64.zip"
$electronHash = (Get-Content -LiteralPath (Join-Path $electron 'checksums.json') -Raw | ConvertFrom-Json).$electronArchive
if ($electronHash -notmatch '^[0-9a-fA-F]{64}$') { throw "Missing Electron checksum for $electronArchive." }
$electronZip = Join-Path $stage $electronArchive
Get-BuildArchive "https://github.com/electron/electron/releases/download/v$electronVersion/$electronArchive" $electronZip $electronHash
Expand-BuildZip $electronZip (Join-Path $electron 'dist')

& (Join-Path $PSScriptRoot 'Build.ps1') -Zig $Zig -SdkHeaders $SdkHeaders -OutputDirectory $native
$runtime = Join-Path $OutputDirectory 'app'
& node (Join-Path $desktop 'package-app.cjs') $stage $runtime $native
if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
& (Join-Path $PSScriptRoot 'Build-VoiceIsolation.ps1') -OutputDirectory (Join-Path $runtime 'resources\voice-isolation')
Copy-Item -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) 'docs\third-party\NAudio-LICENSE.txt') `
    -Destination (Join-Path $runtime 'resources') -Force

if (!$Zig) { $Zig = Join-Path $PSScriptRoot 'obj\toolchain\zig-x86_64-windows-0.15.2\zig.exe' }
$Zig = (Resolve-Path -LiteralPath $Zig).Path
$resource = Join-Path $native 'launcher.res'
$version = [version](Get-Content -LiteralPath (Join-Path $desktop 'package.json') -Raw | ConvertFrom-Json).version
$fileVersion = @($version.Major, $version.Minor, $version.Build, 0) -join ','
& $Zig rc /c65001 "/dSONADESK_VERSION=$fileVersion" "/dSONADESK_VERSION_TEXT=$version" `
    "/I$desktop" "/fo$resource" (Join-Path $desktop 'launcher.rc')
if ($LASTEXITCODE -ne 0) { throw 'Launcher icon compilation failed.' }
$launcher = Join-Path $OutputDirectory 'SonaDesk.exe'
$builtLauncher = Join-Path $native 'SonaDesk.exe'
& $Zig cc -target x86_64-windows-gnu -municode '-Wl,--subsystem,windows' -Oz -s (Join-Path $desktop 'launcher.c') $resource -luser32 -o $builtLauncher
if ($LASTEXITCODE -ne 0) { throw 'Launcher build failed.' }
Copy-Item -LiteralPath $builtLauncher -Destination $launcher -Force
Write-Output $launcher
