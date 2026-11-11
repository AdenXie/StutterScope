param([ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.2.0')
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'tools\fetch-presentmon.ps1')
if ($LASTEXITCODE -ne 0) { throw 'PresentMon verification failed' }
$dist = Join-Path $PSScriptRoot 'dist'
$bundle = Join-Path $dist ('work-' + [Guid]::NewGuid().ToString('N') + '\StutterScope')
New-Item -ItemType Directory -Path (Join-Path $bundle 'tools') -Force | Out-Null
& (Join-Path $PSScriptRoot 'build.ps1') -OutputPath (Join-Path $bundle 'StutterScope.exe')
# Allowlist only: never copy recordings, test output, Git metadata or local settings.
foreach ($file in @('README.md','LICENSE','THIRD_PARTY_NOTICES.md','report-template.html','DeepTrace.wprp','Start-Monitor.ps1','开始记录.cmd','查看最新报告.cmd')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $bundle $file)
}
foreach ($file in @('PresentMon-2.6.0-x64.exe','PresentMon-LICENSE.txt')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('tools\' + $file)) -Destination (Join-Path $bundle ('tools\' + $file))
}
$manifest = @(Get-ChildItem -LiteralPath $bundle -File -Recurse | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($bundle.Length + 1).Replace('\','/')
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $relative
})
[IO.File]::WriteAllLines((Join-Path $bundle 'SHA256SUMS.txt'), $manifest, [Text.UTF8Encoding]::new($false))
$archive = Join-Path $dist "StutterScope-$Version-win-x64.zip"
Compress-Archive -LiteralPath $bundle -DestinationPath $archive -Force
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($archive + '.sha256'), ($hash + '  ' + [IO.Path]::GetFileName($archive) + "`n"), [Text.UTF8Encoding]::new($false))
Write-Output "Portable release: $archive"
