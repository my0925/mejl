param(
    [string]$OutputName = '每日果效记录_实际天数版.exe',
    [string]$TestOutputName = 'DailyTracker.Tests.exe'
)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
Push-Location $PSScriptRoot
try {
    $common = @(
        '/nologo', '/platform:anycpu', '/optimize+', '/checked+', '/highentropyva+', '/warnaserror+',
        '/utf8output', '/codepage:65001', '/win32manifest:app.manifest', '/main:MuYu.DailyTracker.Program',
        '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll',
        '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll',
        '/reference:System.IO.Compression.dll', '/reference:System.IO.Compression.FileSystem.dll'
    )
    $productionSources = @('DailyTracker.cs', 'ImageAttachments.cs', 'TrackerSettings.cs')
    $testSources = @('DailyTracker.cs', 'ImageAttachments.cs', 'TrackerSettings.cs', 'PhotoTests.cs', 'SettingsTests.cs', 'SettlementTests.cs')

    & $compiler @common /target:winexe "/out:$OutputName" @productionSources
    if ($LASTEXITCODE -ne 0) { throw '编译失败' }
    & $compiler @common /target:exe /define:SELF_TESTS "/out:$TestOutputName" @testSources
    if ($LASTEXITCODE -ne 0) { throw '测试程序编译失败' }
    Write-Output "主程序编译完成：$OutputName"
    Write-Output "测试程序编译完成：$TestOutputName"
} finally { Pop-Location }
