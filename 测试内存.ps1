param(
    [string]$TargetExe = (Join-Path $PSScriptRoot 'dist\Windows更新开关.exe'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist\memory-test'),
    [int]$Scans = 20000
)
$ErrorActionPreference = 'Stop'
$target = (Resolve-Path -LiteralPath $TargetExe).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$probe = Join-Path $output 'MemoryProbe.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 ('/out:'+$probe) /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'tests\MemoryProbe.cs')
if($LASTEXITCODE -ne 0) { throw 'Memory probe compilation failed' }
& $probe $target $output $Scans
if($LASTEXITCODE -ne 0) { throw 'Read-only memory regression failed; inspect partial CSV and stderr' }
Get-FileHash -LiteralPath $target -Algorithm SHA256 | Select-Object Path,Hash | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'target-sha256.json') -Encoding UTF8
