param([switch]$Deep, [switch]$CheckOnly)
$ErrorActionPreference = 'Stop'
try {
    if (-not [Environment]::Is64BitOperatingSystem) { throw 'This release requires 64-bit Windows.' }
    if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64') { throw 'This x64 release has not been validated on Windows ARM64.' }
    $runtime = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue
    if (-not $runtime -or $runtime.Release -lt 528040) { throw '.NET Framework 4.8 or later is required.' }
    foreach ($name in @('StutterScope.exe','report-template.html','tools\PresentMon-2.6.0-x64.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $name))) { throw "Missing $name. Extract the full portable release ZIP, or follow the source build instructions." }
    }
    if ($Deep) {
        if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'DeepTrace.wprp'))) { throw 'DeepTrace.wprp is missing.' }
        if (-not (Test-Path -LiteralPath (Join-Path $env:WINDIR 'System32\wpr.exe'))) { throw 'Windows Performance Recorder (wpr.exe) is unavailable on this Windows installation.' }
    }
    if ($CheckOnly) { Write-Output 'Required runtime files and .NET Framework are available. No capture was started.'; exit 0 }
    $launchOptions = @{ FilePath = (Join-Path $PSScriptRoot 'StutterScope.exe'); WorkingDirectory = $PSScriptRoot; Verb = 'RunAs' }
    if ($Deep) { $launchOptions.ArgumentList = '--deep' }
    Start-Process @launchOptions
} catch {
    Write-Host 'Monitor was not started. Check the requirements below; frame capture needs administrator approval.'
    Write-Host $_.Exception.Message
    exit 1
}
