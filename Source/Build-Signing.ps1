function Invoke-BuildSigning([string]$SigningScript, [string[]]$Paths) {
    if (!$SigningScript) { return }

    $files = @($Paths | ForEach-Object { (Resolve-Path -LiteralPath $_).Path })
    $unsigned = @($files | Where-Object {
        (Get-AuthenticodeSignature -LiteralPath $_).Status -ne 'Valid'
    })
    if ($unsigned.Count) {
        & $SigningScript -Paths $unsigned
    }

    foreach ($file in $files) {
        $signature = Get-AuthenticodeSignature -LiteralPath $file
        if ($signature.Status -ne 'Valid') {
            throw "Signing did not produce a trusted signature for $file ($($signature.Status))."
        }
    }
}
