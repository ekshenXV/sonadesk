[CmdletBinding()]
param(
    [string]$Zig,
    [string]$SdkHeaders,
    [string]$OutputDirectory = (Split-Path $PSScriptRoot -Parent)
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$output = Join-Path (Resolve-Path -LiteralPath $OutputDirectory).Path 'MicEffects.zip'
$pending = "$output.$([Guid]::NewGuid()).pending"
$stageRoot = Join-Path $PSScriptRoot 'obj\desktop'
$stage = Join-Path $stageRoot ([Guid]::NewGuid().ToString('N'))
$application = Join-Path $stage 'Mic Effects'
New-Item -ItemType Directory -Path $application -Force | Out-Null
try {
    & (Join-Path $PSScriptRoot 'Build-Desktop.ps1') -Zig $Zig -SdkHeaders $SdkHeaders -OutputDirectory $application
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($application, $pending, [IO.Compression.CompressionLevel]::Optimal, $true)
    if (Test-Path -LiteralPath $output) {
        [IO.File]::Replace($pending, $output, [NullString]::Value)
    } else {
        [IO.File]::Move($pending, $output)
    }
} finally {
    if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending }
    $resolvedStage = (Resolve-Path -LiteralPath $stage).Path
    $resolvedRoot = (Resolve-Path -LiteralPath $stageRoot).Path
    if ([IO.Path]::GetDirectoryName($resolvedStage) -eq $resolvedRoot -and
        !((Get-Item -LiteralPath $resolvedStage).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
Write-Output $output
