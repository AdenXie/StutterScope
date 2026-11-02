param([string]$OutputPath = (Join-Path $PSScriptRoot 'StutterScope.exe'))
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The Windows x64 .NET Framework C# compiler was not found.' }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
& $compiler /nologo /target:exe /platform:x64 /optimize+ /utf8output /codepage:65001 "/win32manifest:$PSScriptRoot\app.manifest" "/out:$OutputPath" /reference:System.Web.Extensions.dll /reference:System.Management.dll /reference:System.Core.dll $sources
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed' }
Write-Output "Built $OutputPath"
