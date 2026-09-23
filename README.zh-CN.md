# Windows 更新开关

这个工具是为 Codex 长时间目标模式、模型训练等长任务而做，用来避免 Windows 自动更新中途打断工作。它可在本机管理 Windows 和 Microsoft Store 自动更新：开始长任务前点击“禁用更新”，需要维护时点击“开启更新”。

## 下载

从 [GitHub Releases](https://github.com/hybtc8888/windows-update-switch/releases) 下载 x64 程序。运行要求为 Windows 11 x64 和系统自带的 .NET Framework 4.8。

界面使用“EN / 中文”按钮切换语言，并为当前 Windows 账户保存选择。切换更新状态时，Windows 会提示管理员授权。

程序尚未进行代码签名，因此 Windows 可能显示发布者未知。

## 管理范围

- Windows 更新服务及相关计划任务。
- Microsoft Store 更新服务、任务与自动更新策略。
- 可让 Windows 恢复更新服务的访问权限。

开启保护时，本机 SYSTEM 后台服务约每 10 秒检查一次；保护会随 Windows 启动，关闭程序窗口后继续运行。只有守护正在运行、60 秒内完成过核验、所有受管项均处于目标状态时，界面才显示保护正常。

点击“开启更新”可恢复服务、任务、策略与权限的原始设置。保护前本来就禁用的项目会保持禁用。

正在安装或等待重启的更新无法撤销。Windows 升级、系统维护或高权限工具可能更改这些设置。本工具不能保证永远阻止所有更新或重启。建议安排维护，并在 Windows 升级后检查保护状态。

## 本机数据

用户目录只需一个 EXE。开启保护后，守护程序安装在 `%ProgramFiles%\LocalWindowsUpdateGuard`，恢复配置和核验记录保存在 `%ProgramData%\LocalWindowsUpdateGuard`；语言选项保存在当前用户的注册表配置中。程序不联网，也不收集遥测。

卸载前先点击“开启更新”并等待恢复完成，再用管理员 PowerShell 执行 `sc.exe delete LocalWindowsUpdateGuard`。随后删除 `%ProgramFiles%\LocalWindowsUpdateGuard`、`%ProgramData%\LocalWindowsUpdateGuard` 和用户目录中的 EXE。恢复成功前请保留恢复数据。

## 从源码构建

无需 NuGet 软件包或第三方运行时。Windows x64 上运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\编译.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\测试.ps1
```

构建使用 Windows 自带的 .NET Framework C# 编译器。测试使用模拟平台，不修改 Windows 更新设置。

## 许可

MIT 许可，详见 [LICENSE](LICENSE)。

[Read in English](README.md)

界面的米白网格配色参考 [Codex Env Sync](https://github.com/hybtc8888/codex-env-sync)，程序与更新控制逻辑由本项目独立实现。
