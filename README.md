# ScreenLight 屏幕亮度调节

一个适用于 Windows 的轻量屏幕亮度工具，支持笔记本和外接显示器分别调节、自定义全局快捷键以及系统托盘运行。

需要 Windows 10 / 11 和 .NET 8 Windows Desktop Runtime。构建后双击 `dist\ScreenLight.exe` 启动。复制到其他电脑时需要复制整个 `dist` 文件夹。

本程序通过 Windows WMI 调节笔记本内置屏幕，通过 DDC/CI 调节外接显示器的真实背光。不使用遮罩变暗。启动时只读取当前亮度，不自动改变亮度。

## 已验证的设备

- 外接屏幕：SANC G7 Pro Max，DP 连接，DDC/CI 真实亮度控制正常。
- 笔记本内置屏幕：CMN1540，WMI 真实亮度控制正常。
- 两块屏幕均实测 100% → 95% → 100%，重新读取验证成功。
- 快捷键注册、重复快捷键拒绝及注销后重注册检查通过。
- 主界面和快捷键界面已渲染检查，程序可正常退出。

## 默认快捷键

| 控制目标 | 提高亮度 | 降低亮度 |
| --- | --- | --- |
| 全部屏幕 | Ctrl + Alt + ↑ | Ctrl + Alt + ↓ |
| 外接屏幕 | Ctrl + Alt + Page Up | Ctrl + Alt + Page Down |
| 笔记本屏幕 | Ctrl + Alt + Shift + ↑ | Ctrl + Alt + Shift + ↓ |
| 鼠标所在屏幕（默认关闭） | Ctrl + Alt + Shift + Page Up | Ctrl + Alt + Shift + Page Down |

默认每次调节 5%，在“快捷键”页面可改为 1%–25%。点击组合键框，再按自己想用的组合键，最后点击“保存并启用快捷键”。取消勾选可以停用某项快捷键。

如果键盘把 Page Up / Page Down 放在 Fn 组合键上，需要按相应的 Fn 组合，或者在软件中换成更方便的快捷键。

关闭窗口后程序留在系统托盘，快捷键仍有效；右键托盘图标或点击“退出程序”才能完全退出。重复启动会打开已有窗口。当前版本不自动配置开机启动。

设置保存在 `%LOCALAPPDATA%\ScreenLight\settings.json`。不保存或自动恢复硬件亮度，以免覆盖显示器实体按键或笔记本 Fn 键的调整。通过快捷键调整时会先读取当前硬件值。

## Twinkle Tray 的排查结果

本项目起因是 Twinkle Tray 提示找不到兼容显示器。独立检测和实际读写验证发现，测试设备的 DDC/CI 与 WMI 接口均可正常工作。软件检测失败并不一定表示显示器不支持亮度调节，但本工具同样需要可用的硬件控制接口。

本程序不修改 Twinkle Tray 的设置、安装文件或快捷键。使用 ScreenLight 时建议先退出 Twinkle Tray，避免两者同时占用快捷键或发送显示器控制命令。

Twinkle Tray 官方排查说明：
https://github.com/xanderfrangos/twinkle-tray/wiki/Display-Detection-%26-Support-Issues

## 如果以后检测失败

点击“重新检测”。显示器插拔或休眠唤醒后也会自动检测。如果仍不可用，查看“检测与帮助”页的错误信息，可导出 JSON 检测报告。

检查显示器菜单内 DDC/CI 是否开启、DP/HDMI 线材和连接是否正常，退出其他亮度控制软件，再尝试显示器断电重启或检查显卡驱动。接口失效时程序会说明错误，不会把失败的写入显示成成功。当前版本在接口可用的情况下工作，不能绕过失效的驱动或显示器 DDC/CI 开关。

克隆/镜像显示可能让多个物理屏幕共享一个桌面区域；这种情况下“鼠标所在屏幕”会调整该区域内所有可控制的物理屏幕。需要精确区分时建议使用扩展桌面。

## 从源码构建

使用 .NET 8 SDK（Windows）运行：

```powershell
dotnet build -c Release
dotnet publish -c Release --self-contained false -o dist
```

没有第三方 NuGet 包。代码通过系统 Win32 API 和 WMI COM 接口控制亮度。DDC 操作在后台串行执行，刷新时释放物理显示器句柄，避免旧句柄被重复使用。

GitHub Actions 会在推送后构建 Windows 版本，可在成功运行的工作流中下载 `ScreenLight-windows` 构建产物。

诊断和验证命令：

```powershell
.\dist\ScreenLight.exe --diagnose diagnostics.json
.\dist\ScreenLight.exe --self-test self-test.txt
.\dist\ScreenLight.exe --ui-check ui-preview.png
```

`--diagnose` 只读硬件，不改亮度；`--ui-check` 临时预览界面，不注册快捷键或修改亮度；`--self-test` 包含真实快捷键注册检查，所以应先退出正常运行的 ScreenLight。WinExe 命令从 PowerShell 运行时可能异步返回，脚本中可使用 `Start-Process -Wait`。

另外提供 `--verify-hardware hardware-verification.json`，它会短暂改变每块屏幕的亮度并在 finally 中尝试恢复；正常使用不需要运行该命令。
