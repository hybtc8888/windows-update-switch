# Windows Update Switch

Built to keep Windows updates from interrupting long-running Codex goal-mode tasks, model training, and other extended work. This small local utility controls automatic Windows and Microsoft Store updates: select **Disable updates** before a long run and **Enable updates** for maintenance.

## Download

Download the x64 executable from [GitHub Releases](https://github.com/hybtc8888/windows-update-switch/releases). Windows 11 x64 and its built-in .NET Framework 4.8 are required.

The app starts without administrator permission. Windows asks for UAC approval when you change the update mode. Use **EN / 中文** in the top-right corner to switch the interface language; the selection is saved for your Windows account.

The executable is not code-signed, so Windows may identify its publisher as unknown.

## What it controls

- Windows Update services and related scheduled tasks.
- Microsoft Store update service, tasks, and automatic-update policy.
- Access controls that can let Windows restore update services.

A LocalSystem service checks these settings about every 10 seconds while protection is enabled. It starts with Windows and continues after the app window closes. The app only displays **protected** when the guard is running, its last check is under 60 seconds old, and every managed item is in its intended state.

Click **Enable updates** to restore the original service, task, policy, and access-control settings. Items that were already disabled before protection remain disabled.

The app will not interrupt an update that has already started or remove a pending restart. Windows upgrades, servicing, or elevated tools can change these settings. This utility cannot guarantee that every update or restart will always be blocked. Schedule maintenance and verify protection after Windows upgrades.

## Local data

The user-facing app is a single executable. When protection is enabled, it installs its worker under `%ProgramFiles%\LocalWindowsUpdateGuard`. Protected settings and a recovery snapshot are stored in `%ProgramData%\LocalWindowsUpdateGuard`; a small language preference is saved under the current user's registry profile. There are no network requests or telemetry.

To remove the utility, first click **Enable updates** and wait for restoration to finish. In an elevated PowerShell window, run `sc.exe delete LocalWindowsUpdateGuard`, then remove `%ProgramFiles%\LocalWindowsUpdateGuard`, `%ProgramData%\LocalWindowsUpdateGuard`, and the app executable. Keep the recovery snapshot until restoration succeeds.

## Build from source

No NuGet packages or third-party runtime libraries are required. From Windows x64, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\编译.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\测试.ps1
```

The build uses the .NET Framework C# compiler included with Windows. Tests use a simulated platform and do not change Windows Update settings.

## License

MIT. See [LICENSE](LICENSE).

[简体中文说明](README.zh-CN.md)

The interface's paper background and grid take visual inspiration from [Codex Env Sync](https://github.com/hybtc8888/codex-env-sync); this project implements its interface and update controls independently.
