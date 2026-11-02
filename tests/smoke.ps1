param([switch]$Quick)
$ErrorActionPreference = 'Stop'
$base = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $base 'StutterScope.exe'
& $exe --self-test
if ($LASTEXITCODE -ne 0) { throw 'Self tests failed' }
$html = [IO.File]::ReadAllText((Join-Path $base 'report-template.html'))
$script = [regex]::Match($html, '(?s)<script>(.*?)</script>').Groups[1].Value
$jsPath = Join-Path $PSScriptRoot 'output\report-script.js'
[IO.File]::WriteAllText($jsPath, $script, [Text.UTF8Encoding]::new($false))
if (Get-Command node -ErrorAction SilentlyContinue) {
    & node --check $jsPath
    if ($LASTEXITCODE -ne 0) { throw 'Report JavaScript syntax check failed' }
}
if ($Quick) { exit 0 }
$root = Join-Path $PSScriptRoot 'output\live-system'
& $exe --capture 8 --system-only --root $root
if ($LASTEXITCODE -ne 0) { throw 'System capture failed' }
$session = Get-ChildItem -LiteralPath $root -Directory | Sort-Object CreationTimeUtc -Descending | Select-Object -First 1
$samples = @(Get-Content -LiteralPath (Join-Path $session.FullName 'system.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
if ($samples.Count -lt 10) { throw 'Too few system samples' }
if (@($samples | Where-Object { $null -ne $_.cpu }).Count -eq 0) { throw 'No CPU counter data' }
Write-Output "PASS actual system capture: $($samples.Count) samples"

$closeRoot = Join-Path $PSScriptRoot 'output\closed-controller'
$stdout = Join-Path $PSScriptRoot 'output\controller-stdout.txt'
$stderr = Join-Path $PSScriptRoot 'output\controller-stderr.txt'
$controller = Start-Process -FilePath $exe -ArgumentList @('--console-no-elevate','--root',('"' + $closeRoot + '"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
try {
    Start-Sleep -Seconds 7
    if ($controller.HasExited) { throw 'Controller exited unexpectedly' }
    $folder = Get-ChildItem -LiteralPath $closeRoot -Directory | Sort-Object CreationTimeUtc -Descending | Select-Object -First 1
    $ids = Get-Content -LiteralPath (Join-Path $folder.FullName 'worker.json') | ConvertFrom-Json
    $worker = Get-Process -Id $ids.pid
    # Kill only this test controller. The detached worker must detect its death and finish by itself.
    Stop-Process -Id $controller.Id -Force
    if (-not $worker.WaitForExit(30000)) { throw "Worker $($worker.Id) did not exit" }
    if (-not (Test-Path -LiteralPath (Join-Path $folder.FullName 'report.html'))) { throw 'No report after closing controller' }
    $meta = Get-Content -Raw -LiteralPath (Join-Path $folder.FullName 'session.json') | ConvertFrom-Json
    if (-not $meta.endUtc) { throw 'Session missing clean end metadata' }
    Write-Output 'PASS detached worker saved report and exited after controller was killed'
} finally {
    $controller.Refresh()
    if (-not $controller.HasExited) { Stop-Process -Id $controller.Id -Force }
    $controller.Dispose()
}
