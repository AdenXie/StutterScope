$ErrorActionPreference = 'Stop'
$name = 'PresentMon-2.6.0-x64.exe'
$expected = 'b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af'
$target = Join-Path $PSScriptRoot $name
if (Test-Path -LiteralPath $target) {
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) {
        throw "Existing $name has an unexpected hash. Inspect or remove it before downloading again."
    }
    Write-Output 'Pinned PresentMon binary verified.'
    exit 0
}
$download = $target + '.download-' + [Guid]::NewGuid().ToString('N')
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/$name" -OutFile $download
if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) {
    throw "Downloaded PresentMon hash mismatch. The untrusted file remains at $download; it was not installed or executed."
}
Move-Item -LiteralPath $download -Destination $target
Write-Output 'Pinned PresentMon downloaded and SHA-256 verified.'
exit 0
