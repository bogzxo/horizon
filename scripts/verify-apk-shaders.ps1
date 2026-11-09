param(
    [Parameter(Mandatory = $true)][string]$Apk,
    [string]$EngineRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)
$ErrorActionPreference = 'Stop'
$packPath = Join-Path $EngineRoot 'Horizon/shaders/packed/horizon.hzshaders'
$builder = Join-Path $EngineRoot 'Horizon.Shaders.Build/bin/Release/net11.0/Horizon.Shaders.Build.exe'
$info = & $builder info --pack $packPath
if ($LASTEXITCODE -ne 0) { throw 'Canonical shader pack is invalid.' }
foreach ($name in @('copy', 'cas', 'upscale_nearest', 'gsr1', 'fsr1_easu', 'fsr1_rcas', 'crt', 'crt_shrink')) {
    if (-not ($info -match "shaders/post/$name\.slang")) { throw "Canonical pack is missing $name." }
}
foreach ($key in @('8671BFA22BC8857E6A6A1C6738F48F6BFE75B7855C3107FF25EA3E2C859F320D', '34C904650DB62F96F472BEBE29EFFE4684795B33469CE7E89B619C70AB8B7F5F')) {
    if (-not ($info -match $key)) { throw "Canonical pack lacks required tablet key $key." }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Apk))
try {
    $entries = @($zip.Entries | Where-Object { $_.FullName.EndsWith('/shaders/packed/horizon.hzshaders') })
    if ($entries.Count -ne 1) { throw "Expected one embedded engine pack, found $($entries.Count)." }
    $stream = $entries[0].Open()
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $actual = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $sha.Dispose() }
    $expected = (Get-FileHash -LiteralPath $packPath -Algorithm SHA256).Hash
    if ($actual -ne $expected) { throw "APK contains a stale pack: $actual; expected $expected. Do not install." }
    # The runtime computes keys from the APK's sources. A new pack beside stale source is still broken.
    $requiredInputs = @{}
    $selected = $false
    foreach ($line in Get-Content -LiteralPath (Join-Path $EngineRoot 'Horizon/shaders.manifest')) {
        $parts = $line.Split("`t")
        if ($parts[0] -in @('file','inline')) {
            $selected = $null -ne $zip.GetEntry("assets/$($parts[4])")
        } elseif ($selected -and $parts[0] -eq 'input') { $requiredInputs[$parts[1]] = $parts[2] }
    }
    foreach ($inputFile in $requiredInputs.GetEnumerator()) {
        $entry = $zip.GetEntry("assets/$($inputFile.Key)")
        if ($null -eq $entry) { throw "APK missing shader source $($inputFile.Key)." }
        $reader = New-Object IO.StreamReader($entry.Open())
        $lines = New-Object 'Collections.Generic.List[string]'
        try { while ($null -ne ($line = $reader.ReadLine())) { $lines.Add($line) } }
        finally { $reader.Dispose() }
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $inputHash = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes([string]::Join("`n", $lines)))).Replace('-', '') }
        finally { $sha.Dispose() }
        if ($inputHash -ne $inputFile.Value) { throw "APK has stale shader source $($inputFile.Key); its runtime key would not match the pack." }
    }
    Write-Output "VERIFIED $Apk"
    Write-Output "Embedded $($entries[0].FullName), $($entries[0].Length) bytes, SHA-256 $actual"
    Write-Output "Verified $($requiredInputs.Count) embedded source/include hashes against the manifest."
    $info | Where-Object { $_ -match 'shaders/post/(copy|cas|upscale_nearest|gsr1|fsr1_easu|fsr1_rcas|crt|crt_shrink)\.slang' }
} finally { $zip.Dispose() }

