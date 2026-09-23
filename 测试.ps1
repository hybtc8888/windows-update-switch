$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('UpdateSwitchTests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    $testExe = Join-Path $scratch 'Tests.exe'
    $arguments = @('/nologo','/target:exe','/platform:x64','/codepage:65001',('/out:' + $testExe))
    $arguments += @('System.dll','System.Core.dll','System.ServiceProcess.dll','System.Web.Extensions.dll','Microsoft.CSharp.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
    $arguments += @('src\Core.cs','src\Windows.cs','src\Status.cs','tests\Tests.cs','tests\StatusTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
    & (Join-Path $framework 'csc.exe') $arguments
    if ($LASTEXITCODE -ne 0) { throw '测试程序编译失败。' }
    & $testExe $scratch
    if ($LASTEXITCODE -ne 0) { throw '测试失败。' }
} finally {
    $resolvedScratch = [IO.Path]::GetFullPath($scratch)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolvedScratch.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolvedScratch -Leaf) -like 'UpdateSwitchTests-*') {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
}
