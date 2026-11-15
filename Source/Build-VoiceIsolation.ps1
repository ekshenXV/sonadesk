param([Parameter(Mandatory = $true)][string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Build-Downloads.ps1')
$cache = Join-Path $PSScriptRoot 'obj\voice-isolation'
New-Item -ItemType Directory -Path $cache, $OutputDirectory -Force | Out-Null

$sherpa = 'sherpa-onnx-v2.0.1-win-x64-shared-MT-Release-no-tts-lib'
$ort = 'onnxruntime-win-x64-MT-Release-1.28.2'
$model = 'dpdfnet2_48khz_hr.onnx'
$sherpaReleases = 'https://github.com/k2-fsa/sherpa-onnx/releases/download'
Get-BuildArchive "$sherpaReleases/v2.0.1/$sherpa.tar.bz2" (Join-Path $cache "$sherpa.tar.bz2") `
    '21DA6A8B92D63CA02AD41DBFC120012343AAAEC2E5E02B14819C6B4F38004509'
Get-BuildArchive "$sherpaReleases/speech-enhancement-models/$model" (Join-Path $cache $model) `
    '0B399F8A58DC4D70D8CD97541F5C39869406145193B957D00A03B66070944928'
Get-BuildArchive "https://github.com/csukuangfj/onnxruntime-libs/releases/download/v1.28.2/$ort.tar.bz2" `
    (Join-Path $cache "$ort.tar.bz2") 'CD7F757F7A38CE14238BC9198BEE197A9430B42EC3ECADEA3408DD2E832EB02E'
Get-BuildArchive 'https://raw.githubusercontent.com/k2-fsa/sherpa-onnx/v2.0.1/LICENSE' `
    (Join-Path $cache 'SHERPA-LICENSE.txt') 'CFC7749B96F63BD31C3C42B5C471BF756814053E847C10F3EB003417BC523D30'
Get-BuildArchive 'https://raw.githubusercontent.com/ceva-ip/DPDFNet/9bd9844a227bb6aa57e55588d8d0e961fcff1c46/LICENSE' `
    (Join-Path $cache 'DPDFNET-LICENSE.txt') 'C71D239DF91726FC519C6EB72D318EC65820627232B2F796219E87DCF35D0AB4'

$dlls = 'sherpa-onnx-c-api.dll', 'onnxruntime.dll', 'onnxruntime_providers_shared.dll'
$entries = $dlls | ForEach-Object { "$sherpa/lib/$_" }
& tar.exe -xjf (Join-Path $cache "$sherpa.tar.bz2") -C $cache @entries
if ($LASTEXITCODE -ne 0) { throw 'Could not extract the voice isolation runtime.' }
& tar.exe -xjf (Join-Path $cache "$ort.tar.bz2") -C $cache "$ort/LICENSE" "$ort/ThirdPartyNotices.txt"
if ($LASTEXITCODE -ne 0) { throw 'Could not extract the voice isolation notices.' }
foreach ($dll in $dlls) {
    Copy-Item -LiteralPath (Join-Path $cache "$sherpa/lib/$dll") -Destination $OutputDirectory -Force
}
Copy-Item -LiteralPath (Join-Path $cache "$ort/LICENSE") -Destination (Join-Path $OutputDirectory 'ONNXRUNTIME-LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $cache "$ort/ThirdPartyNotices.txt") -Destination (Join-Path $OutputDirectory 'ONNXRUNTIME-ThirdPartyNotices.txt') -Force
foreach ($file in @($model, 'SHERPA-LICENSE.txt', 'DPDFNET-LICENSE.txt')) {
    Copy-Item -LiteralPath (Join-Path $cache $file) -Destination $OutputDirectory -Force
}
