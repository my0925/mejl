param([string]$OutputName = '每日果效记录_实际天数版.exe')
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
Push-Location $PSScriptRoot
try {
    & $compiler /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /codepage:65001 /win32manifest:app.manifest /main:MuYu.DailyTracker.Program "/out:$OutputName" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll DailyTracker.cs ImageAttachments.cs PhotoTests.cs TrackerSettings.cs SettingsTests.cs SettlementTests.cs
    if ($LASTEXITCODE -ne 0) { throw '编译失败' }
    Write-Output "编译完成：$OutputName"
} finally { Pop-Location }
