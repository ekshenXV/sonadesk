[CmdletBinding()]
param(
    [string]$Zig,
    [string]$SdkHeaders,
    [string]$OutputDirectory = (Split-Path $PSScriptRoot -Parent)
)

$ErrorActionPreference = 'Stop'
$cache = Join-Path $PSScriptRoot 'obj\toolchain'
$native = Join-Path $PSScriptRoot 'obj\native'
New-Item -ItemType Directory -Path $cache, $native -Force | Out-Null

. (Join-Path $PSScriptRoot 'Build-Downloads.ps1')

if (!$Zig) {
    $Zig = Join-Path $cache 'zig-x86_64-windows-0.15.2\zig.exe'
    if (!(Test-Path -LiteralPath $Zig)) {
        $archive = Join-Path $cache 'zig.zip'
        Get-BuildArchive 'https://ziglang.org/download/0.15.2/zig-x86_64-windows-0.15.2.zip' $archive '3A0ED1E8799A2F8CE2A6E6290A9FF22E6906F8227865911FB7DDEDC3CC14CB0C'
        Expand-BuildZip $archive $cache
    }
}

if (!$SdkHeaders) {
    $SdkHeaders = Join-Path $cache 'windows-headers'
    $headers = 'AudioAPOTypes.h', 'audioenginebaseapo.h', 'audioengineextensionapo.h', 'audiomediatype.h', 'mmdeviceapi.h', 'audioclient.h'
    if (@($headers | Where-Object { !(Test-Path -LiteralPath (Join-Path $SdkHeaders $_)) }).Count) {
        $archive = Join-Path $cache 'windows-sdk.zip'
        Get-BuildArchive 'https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.cpp/10.0.26100.7705/microsoft.windows.sdk.cpp.10.0.26100.7705.nupkg' $archive 'FF455660BEE071A744715A9665666FA29C07681579D3D815959A9EEC5A556424'
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        New-Item -ItemType Directory -Path $SdkHeaders -Force | Out-Null
        $zip = [IO.Compression.ZipFile]::OpenRead($archive)
        try {
            foreach ($header in $headers) {
                $entry = $zip.GetEntry("c/Include/10.0.26100.0/um/$header")
                if (!$entry) { $entry = $zip.Entries | Where-Object Name -eq $header | Select-Object -First 1 }
                if (!$entry) { throw "Missing Windows SDK header $header" }
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $SdkHeaders $header), $true)
            }
        } finally { $zip.Dispose() }
    }
}

$Zig = (Resolve-Path -LiteralPath $Zig).Path
$SdkHeaders = (Resolve-Path -LiteralPath $SdkHeaders).Path
$env:ZIG_GLOBAL_CACHE_DIR = Join-Path $cache 'zig-global'
$env:ZIG_LOCAL_CACHE_DIR = Join-Path $cache 'zig-local'
$version = [version](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Desktop\package.json') -Raw | ConvertFrom-Json).version
$fileVersion = @($version.Major, $version.Minor, $version.Build, 0) -join ','
$apoResource = Join-Path $native 'apo.res'
$vst3Resource = Join-Path $native 'vst3.res'
$component = 1
foreach ($resource in @($apoResource, $vst3Resource)) {
    & $Zig rc /c65001 "/dSONADESK_VERSION=$fileVersion" "/dSONADESK_VERSION_TEXT=$version" `
        "/dSONADESK_COMPONENT=$component" "/fo$resource" (Join-Path $PSScriptRoot 'NativeVersion.rc')
    if ($LASTEXITCODE -ne 0) { throw 'Native version resource compilation failed.' }
    $component++
}
$dll = Join-Path $native 'MicEffects.Apo.dll'
& $Zig c++ -target x86_64-windows-gnu -std=c++17 -fms-extensions -shared -fno-exceptions -fno-rtti -O2 -g0 -s -DUNICODE -D_UNICODE -I $SdkHeaders (Join-Path $PSScriptRoot 'Apo\MicApo.cpp') (Join-Path $PSScriptRoot 'Apo\MicApo.def') $apoResource -lole32 -ladvapi32 -o $dll
if ($LASTEXITCODE -ne 0) { throw 'APO build failed.' }

$vst3Sdk = Join-Path $cache 'vst3-sdk'
$vst3Revision = '4f547e8e102b47de4a8b8aaf343c73b700786372'
$interfaces = Join-Path $vst3Sdk 'pluginterfaces'
if (!(Test-Path -LiteralPath (Join-Path $interfaces 'vst\ivstaudioprocessor.h'))) {
    $archive = Join-Path $cache 'vst3-interfaces.zip'
    Get-BuildArchive "https://codeload.github.com/steinbergmedia/vst3_pluginterfaces/zip/$vst3Revision" $archive `
        '6C7EE5E567E9C84F22A5BD037BBD58E3DC922E985E4AB27B583E7DACAEF51409'
    Expand-BuildZip $archive $vst3Sdk
    $extracted = Join-Path $vst3Sdk "vst3_pluginterfaces-$vst3Revision"
    New-Item -ItemType Directory -Path $interfaces -Force | Out-Null
    Copy-Item -Path "$extracted\*" -Destination $interfaces -Recurse -Force
}
$vst3Host = Join-Path $native 'MicEffects.Vst3.dll'
& $Zig c++ -target x86_64-windows-gnu -std=c++17 -shared -fno-rtti -O2 -g0 -s -I $vst3Sdk `
    (Join-Path $PSScriptRoot 'Vst3\Host.cpp') $vst3Resource -luser32 -o $vst3Host
if ($LASTEXITCODE -ne 0) { throw 'VST3 host build failed.' }

dotnet build (Join-Path $PSScriptRoot 'MicEffects.csproj') -c Release "-p:ApoBinary=$dll"
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bin\Release\net48\SonaDesk.Audio.exe') -Destination $OutputDirectory -Force
Copy-Item -LiteralPath $vst3Host -Destination $OutputDirectory -Force
