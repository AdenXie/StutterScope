param([switch]$Light, [switch]$Deep, [switch]$CheckOnly)  # -Deep is accepted for old shortcuts; event-level tracing is now on by default.
$ErrorActionPreference = 'Stop'
try {
    if (-not [Environment]::Is64BitOperatingSystem) { throw 'This release requires 64-bit Windows.' }
    if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64') { throw 'This x64 release has not been validated on Windows ARM64.' }
    $runtime = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue
    if (-not $runtime -or $runtime.Release -lt 528040) { throw '.NET Framework 4.8 or later is required.' }
    foreach ($name in @('StutterScope.exe','report-template.html','tools\PresentMon-2.6.0-x64.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $name))) { throw "Missing $name. Extract the full portable release ZIP, or follow the source build instructions." }
    }
    # Event-level tracing is optional: without these the recorder still captures frames and system telemetry.
    if (-not $Light) {
        if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'DeepTrace.wprp'))) { Write-Host 'Note: DeepTrace.wprp is missing; event-level tracing will be skipped.' }
        elseif (-not (Test-Path -LiteralPath (Join-Path $env:WINDIR 'System32\wpr.exe'))) { Write-Host 'Note: wpr.exe is unavailable; event-level tracing will be skipped.' }
    }
    if ($CheckOnly) { Write-Output 'Required runtime files and .NET Framework are available. No capture was started.'; exit 0 }
    $launchOptions = @{ FilePath = (Join-Path $PSScriptRoot 'StutterScope.exe'); WorkingDirectory = $PSScriptRoot; Verb = 'RunAs' }
    if ($Light) { $launchOptions.ArgumentList = '--light' }
    Start-Process @launchOptions
} catch {
    Write-Host 'Monitor was not started. Check the requirements below; frame capture needs administrator approval.'
    Write-Host $_.Exception.Message
    exit 1
}
