# 每日果效记录

Windows 单机版每日数量记录工具，支持历史记录按月折叠、周/月结算、每周扣减、图片附件、CSV 导出和完整数据备份。

本仓库只包含 `每日果效记录_实际天数版.exe` 对应的主程序源码。

## 项目内容

- `DailyTracker.cs`：数据模型、历史记录、结算、主界面和基础测试。
- `ImageAttachments.cs`：图片附件、周/月图片导出和备份恢复。
- `TrackerSettings.cs`：折合比例、评价阈值和每周扣减设置。
- `PhotoTests.cs`：图片与结算显示测试。
- `SettingsTests.cs`：设置和旧数据兼容测试。
- `SettlementTests.cs`：周/月结算及扣减测试。
- `app.manifest`：Windows 应用清单。
- `编译.ps1`：主程序构建脚本。

编译生成的 EXE、ZIP 和测试输出不会提交到仓库。

## 构建

要求：仍在微软安全支持期内的 Windows 11、PowerShell、.NET Framework 4.8.1，并通过 Windows Update 安装最新安全更新。

```powershell
.\编译.ps1
```

默认生成：

- `每日果效记录_实际天数版.exe`：正式主程序，不包含自测入口或测试类。
- `DailyTracker.Tests.exe`：仅用于本地回归验证，不进入发布包。

## 运行回归测试

先运行构建，再使用测试程序执行：

```powershell
$exe = '.\DailyTracker.Tests.exe'
& $exe --self-test '.\验证结果\core'
& $exe --photo-self-test '.\验证结果\photo'
& $exe --settings-self-test '.\验证结果\settings'
& $exe --settlement-self-test '.\验证结果\settlement'
& $exe --ui-self-test '.\验证结果\ui'
```

## 数据说明

个人记录保存在当前 Windows 用户的 `%LOCALAPPDATA%\MuYuDailyTracker` 目录。仓库不会包含个人记录、图片、本机凭据或缓存。
