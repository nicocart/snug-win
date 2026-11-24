## SnugWin v{{VERSION}}

Windows 10/11 上为 **网易 UU远程** 与 **CrossDesk** 提供手机原生级操作体验的伴侣程序。承接 [zephyr4123/snug](https://github.com/zephyr4123/snug)（MIT，v2.8.0）的多屏多机型、多远控适配与交互状态机，C# / .NET 10 LTS / WPF + virtual-display-rs。

### 下载

| 资产 | 说明 |
| --- | --- |
| `SnugWin-v{{VERSION}}-win-x64-setup.exe` | **一键安装包**（推荐新手）：内置 .NET 10 运行时、驱动校验安装脚本、桌面快捷方式与可选开机自启 |
| `SnugWin-v{{VERSION}}-win-x64-selfcontained.zip` | 绿色便携自包含版，内置 .NET 10 运行时与虚拟显示器驱动（`driver\`），解压即用并支持应用内一键装驱与热更新 |
| `SnugWin-v{{VERSION}}-win-x64-frameworkdependent.zip` | 极小体积，内置虚拟显示器驱动（`driver\`），需先装 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0/runtime/desktop) |
| `virtual-display-driver-0.3.1-x64.zip` | 独立虚拟显示器驱动包（备用；各安装包与便携包均已内置） |

### 安装两步

```text
1. 安装包版：运行 setup.exe，一路默认即可完成装驱与启动；
2. 绿色便携版：解压 zip 后双击 SnugWin.App.exe（允许 UAC），若尚未装过驱动，直接在设置页顶部点击「立即一键安装驱动」（内置 SHA256 与证书指纹校验，不误删原有同款驱动）
```

每个包内都附带 `快速开始.txt`。

### 本版变化

{{COMMITS}}

完整差异：https://github.com/nicocart/snug-win/compare/{{PREVIOUS}}...v{{VERSION}}

### 前置条件

- Windows 10 2004+ / Windows 11，x64
- [virtual-display-rs](https://github.com/MolotovCherry/virtual-display-rs) 0.3.1（本 Release 已附带）
- 管理员权限（清单为 `requireAdministrator` + PerMonitorV2）

### 已知限制

- 在 Windows 11 x64 + UU远程 / CrossDesk 实测；Windows 10 2004+ 理论可用但未逐一验证
- 依赖 virtual-display-rs 的命名管道 `\\.\pipe\virtualdisplaydriver`
- 多屏场景支持同时开启最多 4 块虚拟屏（手机 / 平板 / 电脑），各屏独立管理；物理显示器上的同款驱动屏幕受 `OwnedDisplayBook` 隔离保护
- 换机型比例或远控启动后新增虚拟屏，需重启仅在启动时枚举显示器的远控（如 CrossDesk）或在远控侧重选屏幕，点按位置才准确（设置页支持「一键重启远控」）

### 致谢与许可

- 上游 macOS 原版：[zephyr4123/snug](https://github.com/zephyr4123/snug)（v2.8.0）
- 虚拟显示器驱动：[MolotovCherry/virtual-display-rs](https://github.com/MolotovCherry/virtual-display-rs)（0.3.1）
- 本项目：MIT License，第三方声明见 [THIRD_PARTY_NOTICES.md](https://github.com/nicocart/snug-win/blob/main/THIRD_PARTY_NOTICES.md)

