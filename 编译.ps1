param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '需要 Windows x64 和 .NET Framework 4.8。' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$output = Join-Path (Resolve-Path -LiteralPath $OutputDirectory).Path 'Windows更新开关.exe'
$references = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.ServiceProcess.dll','System.Web.Extensions.dll','Microsoft.CSharp.dll')
$arguments = @('/nologo','/target:winexe','/platform:x64','/optimize+','/codepage:65001',('/out:' + $output),('/win32manifest:' + (Join-Path $PSScriptRoot 'src\app.manifest')))
$arguments += '/win32icon:' + (Join-Path $PSScriptRoot 'src\应用图标.ico')
$arguments += $references | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$arguments += @('Core.cs','Windows.cs','Status.cs','App.cs') | ForEach-Object { Join-Path $PSScriptRoot ('src\' + $_) }
& $compiler $arguments
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
Write-Output $output
