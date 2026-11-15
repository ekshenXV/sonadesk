function Get-BuildArchive([string]$Url, [string]$Path, [string]$Hash) {
    if (Test-Path -LiteralPath $Path) {
        if ((Get-FileHash -LiteralPath $Path).Hash -eq $Hash) { return }
        throw "Checksum mismatch for $Path"
    }
    $download = "$Path.download"
    try {
        & curl.exe --fail --location --silent --show-error --retry 2 --output $download $Url
        if ($LASTEXITCODE -ne 0 -or (Get-FileHash -LiteralPath $download).Hash -ne $Hash) {
            throw "Download verification failed for $Url"
        }
        Move-Item -LiteralPath $download -Destination $Path
    } finally {
        if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download }
    }
}

function Expand-BuildZip([string]$Path, [string]$Destination) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    & tar.exe -xf $Path -C $Destination
    if ($LASTEXITCODE -ne 0) { throw "Could not extract $Path" }
}
