<div align="center">

**简体中文** · [English](README.en.md)

<img src="Assets/snugwin-logo.png" width="128" alt="SnugWin 图标">

# SnugWin

**让 Windows 在手机与平板上拥有原生级的操作体验**

已支持 **UU远程** · **CrossDesk**<br>最多 4 块专属虚拟屏（手机 / 平板 / 电脑） · 原生触控手势 · App 资源库 · 应用内自动更新

[![最新版本](https://img.shields.io/github/v/release/nicocart/snug-win?label=%E6%9C%80%E6%96%B0%E7%89%88%E6%9C%AC&color=blue)](https://github.com/nicocart/snug-win/releases/latest) [![CI](https://github.com/nicocart/snug-win/actions/workflows/ci.yml/badge.svg)](https://github.com/nicocart/snug-win/actions/workflows/ci.yml) [![Release](https://github.com/nicocart/snug-win/actions/workflows/release.yml/badge.svg)](https://github.com/nicocart/snug-win/actions/workflows/release.yml) ![Windows 10/11 x64](https://img.shields.io/badge/Windows-10%20%2F%2011%20x64-0078D6) ![.NET 10 LTS](https://img.shields.io/badge/.NET-10%20LTS-512BD4) [![许可证 MIT](https://img.shields.io/badge/%E8%AE%B8%E5%8F%AF%E8%AF%81-MIT-green)](LICENSE)

[下载](#-下载) · [快速上手](#-快速上手) · [原生体验](#-原生体验) · [App 资源库](#-app-资源库) · [适配](#-适配所有-app-与手机) · [对比](#-与其他方案的对比) · [安装与更新](#-安装与更新) · [限制](#-已知限制) · [开发](#-开发)

</div>

> [!IMPORTANT]
> **新增 CrossDesk**：已支持免费、开源的远控软件 [CrossDesk](https://github.com/kunkundi/crossdesk)，屏幕、原生手势与 App 资源库同 UU远程；首次使用参见[快速上手](#-快速上手)。
>
> **关于 UU远程 VIP**：UU远程自 2026 年 12 月 1 日起将多屏、虚拟屏等功能纳入 VIP；若你恰为 UU远程 VIP 用户欢迎使用，若需免费方案可搭配 CrossDesk。SnugWin 为独立社区项目，与 UU远程 无商业关联，是否开通 VIP 请依自身对 UU远程 的需求决定。

> 1:1 承接并跟随上游 macOS 原版 [**zephyr4123/snug**](https://github.com/zephyr4123/snug)（MIT License，`v2.8.0`）的产品体验、多屏多远控适配与交互状态机，专为 **Windows 10 / 11 + 网易 UU远程 / CrossDesk** 打造。

---

## 📥 下载

<img src="Assets/snugwin-logo.png" width="88" align="left" alt="SnugWin 图标">

**SnugWin for Windows** [![最新版本](https://img.shields.io/github/v/release/nicocart/snug-win?label=%E6%9C%80%E6%96%B0%E7%89%88%E6%9C%AC&color=blue)](https://github.com/nicocart/snug-win/releases/latest)<br>适用于 Windows 10 (2004+) / Windows 11 x64，基于 .NET 10 LTS + WPF 构建<br>CI 自动构建与 SHA-256 校验发布，提供**一键安装包**与**绿色便携包**，[前往 Releases 下载](https://github.com/nicocart/snug-win/releases/latest)

<br clear="left">

| 资产 | 适用人群 | 说明 |
| :--- | :--- | :--- |
| **`SnugWin-vX.Y.Z-win-x64-setup.exe`** | **新手推荐（一键安装）** | Inno Setup 安装包：内置 .NET 10 运行时、自动校验安装虚拟屏驱动、桌面快捷方式与可选开机静默自启 |
| `SnugWin-vX.Y.Z-win-x64-selfcontained.zip` | 绿色便携 / 免安装 | 内置 .NET 10 运行时与驱动包（`driver\`），解压即用并支持设置页一键装驱与热更新 |
| `SnugWin-vX.Y.Z-win-x64-frameworkdependent.zip` | 极小体积 | 内置驱动包（`driver\`），需本机已装 [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0) |
| `virtual-display-driver-0.3.1-x64.zip` | 独立驱动包（备用） | 单独提供的虚拟显示器驱动 + `install-driver.cmd`（`setup.exe` 与便携版均已内置） |
| `checksums.txt` | 安全校验 | 所有发布资产的 SHA-256 校验和 |

> 从 `v1.2.0` 起，每个版本都同时提供 `setup.exe` 一键安装包（内置驱动安装与自启选项）和绿色便携 zip。

---

## ✨ 原生体验

远程控制软件默认将整块电脑桌面等比压缩进手机屏幕，文字与按钮随之缩小数倍。SnugWin 在手机或平板连入时准备或新建专属虚拟屏（最多支持 4 块手机 / 平板 / 电脑虚拟屏，支持设为常驻屏），让 Windows 窗口以真实移动端尺度呈现，用起来如同原生 App。

### 竖屏与多设备原生显示

* **专属虚拟屏**：支持 **UU远程** 四路探测（GameViewer 覆盖窗、UIA 控制器列表、`GameViewerServer` 推流日志增长与 TCP 辅助诊断）与 **CrossDesk**（支持 `CrossDeskService` 持久宿主日志识别与进程存活检测）；通过 `virtual-display-rs` 命名管道按 SnugWin 归属 ID 创建并只清理自身虚拟屏，按所选机型比例与高 DPI（CCD）呈现。
* **多屏与常驻**：在 **设置 → 多屏管理** 可同时管理最多 4 块虚拟屏（手机 / 平板 / 电脑），支持将单块虚拟屏「设为常驻屏」，或开启「提前准备虚拟屏」以配合仅在启动时认屏的远控（如 CrossDesk）。
* **同会话重进手机屏**：在手机端退出手机屏后，本次会话期间会在主屏右下角保留轻量化 **「进入手机屏」** 悬浮胶囊并在托盘提供重进入口，随时一键恢复竖屏。
* **窗口铺满**：选中的窗口通过 DWM 扩展边框补偿精确贴满手机屏。
* **字号放大**：终端、VS Code、Cursor 等编辑器自动放大字号，按 App 分别记忆，并在恢复原位时自动缩回。

### 原生手势与悬浮操作条

远程客户端手机端常沿用触控板鼠标逻辑；SnugWin 通过独立高优先级 `WH_MOUSE_LL` 钩子 + 异步重放队列（兼容 CrossDesk 隐私输入守卫标记透传），在虚拟触屏内按手机直觉重新翻译触控：

| 手机上的操作 | 原版远控 | SnugWin 虚拟屏 |
| :--- | :--- | :--- |
| 单指滑动 | 鼠标拖拽，容易误选文字 | 页面平滑滚动，松手带 60fps 惯性 |
| 长按 0.5 秒后拖动 | 鼠标拖拽 | 精确还原按压原点划选文字；松手弹出 **「复制 \| 粘贴 \| 删除」** 悬浮操作条 |
| 原地长按松开 | 普通左键点击 | 调出右键菜单（可在设置中单独关闭，避免 PowerShell 等终端误触发右键粘贴） |
| 浏览器标签栏点击 | — | Edge / Chrome / Firefox 等原生标签页区域直通点击，零延迟切换 |
| 仅接管注入事件 | — | 默认只翻译远控注入的触控，本机物理鼠标跨入虚拟屏仍保持原生操控（可在设置切换） |

> 划选悬浮操作条会自动识别当前窗口类型：**复制**在终端自动发送 `Ctrl+Shift+C`、普通窗口发送 `Ctrl+C`；**粘贴**在 Windows Terminal 自动发送 `Ctrl+Shift+V`、其他应用发送 `Ctrl+V`；**删除**发送 `Backspace` 清除可编辑选区而不覆盖剪贴板。以上手势与操作条均可在设置窗中独立开关。

### 与 iPhone 一致的操作

| iPhone | SnugWin 手机屏 |
| :--- | :--- |
| App 资源库 | [App 资源库](#-app-资源库)：首页，查找、打开与管理电脑上的桌面与 UWP 应用 |
| 底部小横条 | 点击或单指上滑，随时回到 App 资源库 |
| 左上角「‹ 返回」 | 回到当前放在手机屏上的窗口；点击置顶窗口关闭按钮时先归位再关闭 |

### 截图与裁剪

在 SnugWin 设置中开启「相机键」后，底部小横条左侧出现相机图标。轻点即截取当前手机屏内的窗口原画写入 Windows 剪贴板，并在左下角浮出缩略图预览；轻点缩略图可进入 **8 把手裁剪模式**，以裁剪区域替换剪贴板内容。

---

## 📱 App 资源库

手机屏首页专为竖屏指尖操作设计（1:1 复刻 iOS 资源库交互），查找、打开、切换、新建窗口、关闭或退出应用无需回到电脑前：

* **运行中**：按「当前 App / 其他 App」分层卡片展示，窗口无论在主屏、副屏还是最小化均可直接拉上手机屏。
* **全部**：4 列应用图标网格，使用 Win32 `IShellItemImageFactory` 提取 **128×128** 高清透明图标，同时扫描传统桌面程序与 `shell:AppsFolder` 商店/UWP 应用（如 Windows Terminal、设置等）。
* **实时搜索**：匹配应用名与窗口标题，支持中英文、拼音全拼、常见 App 多音字扩展及首字母（如输入 `blbl` 找到「哔哩哔哩」、`bwl` 找到「备忘录」）。

| 操作 | 方式 |
| :--- | :--- |
| 打开窗口 | 轻点窗口卡片即铺满手机屏；轻点 App 激活或拉起最近窗口 |
| 新建窗口 | 轻点 App 卡片右上角「新建窗口」，新窗口直接按手机屏宽度打开 |
| 关闭 / 退出 | 轻点关闭或退出后需再点一次确认，防误触；10 秒未退出时支持强制结束 |
| 退出手机屏 | 轻点右上角「退出手机屏」并确认，窗口归位、手机屏撤除 |

---

## 📐 适配所有 App 与手机

* **任意 App 完整铺满**：对限定最小宽度的应用（如部分 IDE、设置窗），SnugWin 会先尝试手机宽度；若窗口拒绝再窄，则自动把虚拟屏切换到恰好容纳它的同比例档位再铺满，确保内容不裁切、坐标点按准确。
* **全机型比例库**：内置 `Assets/phone-models.json` 覆盖 17 个品牌约 800 款手机/平板竖屏比例，也支持直接在屏内或设置窗输入任意自定义分辨率与宽高比。

---

## 🆚 与其他方案的对比

| 方案 | 局限 | SnugWin |
| :--- | :--- | :--- |
| **AI 助手自带远程控制** | 仅绑定单一 AI 产品或仅限聊天框 | 直接操作电脑原生任意窗口（VS Code、终端、浏览器、微信等全部可用） |
| **UU远程自带扩展终端** | 独立于本机现有终端会话 | 直接操控本机已经打开的终端与开发窗口，工作流无缝接续 |

---

## 📦 安装与更新

## 🚀 快速上手

按所用远控操作；首次设置后，日常直接连接即可。

| 步骤 | UU远程（GameViewer） | CrossDesk |
| :--- | :--- | :--- |
| **1. 启动与准备** | 启动 SnugWin 与 UU远程 | 启动 SnugWin，在 **设置 → 多屏管理** 打开 **「提前准备虚拟屏」** 后，点击设置页顶部 **「一键重启远控」**（或手动重启 `CrossDeskService` 与 CrossDesk，因为 CrossDesk 仅在启动时识别显示器） |
| **2. 发起连接** | 手机端 UU远程 连入本机，SnugWin 自动建出虚拟屏 | 手机端 CrossDesk 连入本机，虚拟屏已在就绪列表 |
| **3. 切换屏幕** | 在手机端 UU远程 的屏幕列表中选择「Snug 手机屏」 | 在手机端 CrossDesk 的屏幕列表中选择「Snug 手机屏」 |
| **4. 触控模式** | 开箱即用（自动翻译单指滑动、长按划选、长按右键） | 在手机端 CrossDesk 会话浮动按钮或设置中将 **「相对鼠标」切换为「绝对鼠标」（绝对位置）** 以启用单指滑动与惯性滚动（即使开启 CrossDesk 隐私屏也能正常点按与滚动） |
| **5. 结束 / 退出** | 断开或「退出手机屏」后窗口归位并撤除虚拟屏（设为「常驻」除外） | 断开后自动把窗口归位回主屏并保留提前准备的屏幕（无人远程时本机鼠标无法滑入）；手机端「退出手机屏」时窗口还原、屏幕保留 |

> **系统要求**：Windows 10（2004+）/ Windows 11 x64，本机已安装 [网易 UU远程（GameViewer）](https://uuyc.163.com/) 或 [CrossDesk](https://github.com/kunkundi/crossdesk)。

### 方式一：一键安装包（推荐）

1. 从 [Releases](https://github.com/nicocart/snug-win/releases/latest) 下载 `SnugWin-vX.Y.Z-win-x64-setup.exe` 并运行；
2. 安装向导内默认勾选「安装 / 校验 `virtual-display-rs 0.3.1` 驱动」，将自动完成压缩包 SHA-256 校验、证书指纹校验（`5DFA7E68997ED52D4B10420407860D1C29B6F427`）与静默安装（若本机已存在共享 `Root\VirtualDisplayDriver` 的 OEM 虚拟屏驱动，会自动通过 `INSTALLFLAG_FORCE` 强绑就绪），并写入 `%ProgramData%\SnugWin\driver-ownership.json` 归属标记（绝不误删你原本自行安装的同款驱动）；
3. 可按需勾选「创建桌面快捷方式」与「开机静默自启动（后台常驻托盘）」。

### 方式二：绿色便携版

1. 下载 `SnugWin-vX.Y.Z-win-x64-selfcontained.zip` 解压到任意固定目录（压缩包已内置 `driver\` 目录）；
2. 双击 `SnugWin.App.exe`（程序清单配置为 `requireAdministrator` + `PerMonitorV2`，允许 UAC 后常驻托盘）；若首次启动提示缺少虚拟屏驱动，直接在设置窗顶部点击 **「立即一键安装驱动」**（或手动右键 `driver\install-driver.cmd` → 以管理员身份运行）。

### 开机自启动与自动更新

* **开机静默自启动**：在设置窗「常规与手机屏」开启开关后，SnugWin 会通过 Windows **任务计划程序（Task Scheduler）** 注册一条 `HighestAvailable` 最高权限的登录启动任务（附带 `--minimized`），开机即可免 UAC 弹窗静默进托盘。
* **应用内检查与热更新**：设置窗「关于与更新」及托盘菜单支持检查 GitHub 最新版本；点击「下载并更新重启」后会自动匹配当前包类型（自包含 / 框架依赖）、先校验 `checksums.txt` 的 SHA-256，再安全归位窗口、替换程序并重启。

---

## 🧭 使用方式

1. 确保电脑端已运行 SnugWin 与 **UU远程** 或 **CrossDesk**（若使用 CrossDesk，建议在「多屏管理」开启「提前准备虚拟屏」并用「一键重启远控」刷新其显示列表）；
2. 手机或平板连入本机后，在远控的屏幕选择器中切换到「Snug 手机屏」（CrossDesk 请在手机端将鼠标模式设为「绝对鼠标」）；
3. 首次使用可在 App 资源库右上角选择手机机型比例（换比例后 UU 侧切一次屏再切回，CrossDesk 点击设置页「一键重启远控」即可精确对齐点按坐标）；
4. 轻点 App 或窗口开始使用；断开远程连接后，SnugWin 自动把窗口还原回原位、恢复字号并按配置撤除或保留虚拟屏。

**固定竖屏像素（可选）**：选好「小米 15 Ultra」并退出 SnugWin，在 `%LocalAppData%\SnugWin\config.json` 的 `Screens` 数组中，仅给该手机竖屏条目加上 `"fixedNativePixels": true`，保存后重启。Windows 虚拟屏只请求 **1440 × 3200**，不随窗口内容改宽；删除该项或设为 `false` 即恢复默认适配。此选项不设置远控的传输分辨率或码率。

固定模式仅适用于手机竖屏：宽 480–2160、高 800–4320 像素，宽高均为偶数，长边不超过短边的 3 倍。仅填比例（如 9 × 20）或超出固定模式范围时仍按默认策略适配；缺少宽高或宽高无效的条目不建屏。

---

## 🔒 隔离与还原

* **只改动虚拟屏**：不修改主显示器分辨率与缩放，未切到虚拟屏时与原版远控完全一致。
* **先记录，后搬窗**：所有窗口的原位置与缩放级数持久化保存在 `%LocalAppData%\SnugWin\phone-screen.json` 与 `left-behind.json`，即使断电或异常退出，下次启动也能自动恢复。
* **不上传任何隐私数据**：除手动或可选开机检查 GitHub Release 新版本外，不连接任何第三方服务器；日志与配置全部仅保存在本地 `%LocalAppData%\SnugWin\`。

---

## 🚧 已知限制

| 限制 | 说明 |
| :--- | :--- |
| 依赖虚拟屏驱动管道 | 需要系统存在 `virtual-display-rs` 命名管道 `\\.\pipe\virtualdisplaydriver`；如未就绪，可在设置页「驱动与日志」点击「安装 / 修复驱动」 |
| 需要管理员权限 | 为跨 UIPI 操控高权限窗口、设置 CCD 显示模式、重启远控服务及接管注入触控，清单需 `requireAdministrator`；正因如此开机自启依赖任务计划而非传统 `HKCU\Run` |
| 多屏与设备并发 | 支持最多同时启用 **4 块虚拟屏**（手机、平板、电脑混合配置）；同一时刻由当前主连接远控接管 |
| 换机型比例后点按对齐 | 更改手机分辨率比例后，UU 侧需切换一次屏幕再切回，CrossDesk 可点击设置页顶部「一键重启远控」重新枚举 |
| 仅 Windows 11/10 x64 | 在 Windows 11 x64 + UU远程 / CrossDesk 实测验证；Windows 10 2004+ 理论支持 |

---

## 🔧 开发

* **技术栈**：C# / `.NET 10 LTS` / WPF (`PerMonitorV2`) + `Win32 / UIAutomation / CCD / WH_MOUSE_LL` + `Inno Setup 6`
* **三层架构**：`SnugWin.Core`（纯逻辑、多远控状态机与触控归一化）→ `SnugWin.Windows`（显示、搬窗、输入钩子、UU / CrossDesk 传感、自启与更新）→ `SnugWin.App`（现代深色 WPF 界面与托盘宿主）

```powershell
# 构建（零警告门禁）并运行全部单元测试
dotnet build SnugWin.sln -c Release -warnaserror
dotnet test SnugWin.sln -c Release --no-build

# 本地发布自包含版
dotnet publish src\SnugWin.App\SnugWin.App.csproj -c Release -r win-x64 --self-contained true -o publish-out
```

* CI / 自动发版由 [`.github/workflows/ci.yml`](.github/workflows/ci.yml) 与 [`.github/workflows/release.yml`](.github/workflows/release.yml) 驱动：推送 `main` 自动跑构建与测试，推送 `v*` tag 自动产出 `setup.exe` 安装包、自包含/框架依赖双 zip、校验过的 `virtual-display-rs` 驱动包与 `checksums.txt`。
* 完整第三方依赖与许可信息见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

---

## 🧹 卸载

1. 若通过安装包安装：从 Windows「设置 → 应用」直接卸载 SnugWin，卸载程序会自动清理 `SnugWinAutoStart` 自启任务计划，并依据 `%ProgramData%\SnugWin\driver-ownership.json` 仅在驱动由 SnugWin 安装时才安全卸载驱动（保留用户原有自装驱动）；
2. 若使用绿色版：先从托盘「退出 SnugWin（先还原窗口）」，关闭设置页「开机静默自启动」后删除程序目录；
3. 可选清理本地配置与日志：`%LocalAppData%\SnugWin\`。

---

## 🙏 致谢

首先，谨向 **[网易 UU远程](https://uuyc.163.com/)** 致以诚挚的敬意——稳定低延迟的免费远程控制能力让手机端随时操控桌面主机成为可能。

同样感谢以下项目与社区工作：

* [**zephyr4123/snug**](https://github.com/zephyr4123/snug)（MIT License）：AoyamaShou 与 zephyr4123，提供全部产品理念、核心状态机模型、多远控架构（含上游贡献者 Nexmoe 的 CrossDesk 初版适配及 李小双、11ze 等社区改进）、双屏叠片图标设计及机型库数据集；
* [**CrossDesk**](https://github.com/kunkundi/crossdesk)（GPL-3.0）：kunkundi，免费开源跨平台远控软件；SnugWin 仅按其公开日志与窗口/输入行为对接，不含其代码；
* [**MolotovCherry/virtual-display-rs**](https://github.com/MolotovCherry/virtual-display-rs)（MIT License）：Windows IddCx 虚拟显示器驱动与命名管道服务；
* **社区贡献者**：感谢 [@WilliamLambertCN](https://github.com/WilliamLambertCN)（PR #1：EDID 虚拟屏身份绑定、IPC 加固、固定竖屏像素与会话采样稳定性）与 [@Jym2004](https://github.com/Jym2004)（PR #2：UIA 控制端读取与离屏唤醒、桌面端筛选、虚拟屏离线恢复与超级屏让路）。

---

## ⭐ Star 趋势

<p align="center"><a href="https://www.star-history.com/#nicocart/snug-win&Date"><picture><source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/svg?repos=nicocart/snug-win&type=Date&theme=dark"><source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/svg?repos=nicocart/snug-win&type=Date"><img src="https://api.star-history.com/svg?repos=nicocart/snug-win&type=Date" width="80%" alt="SnugWin 的 GitHub Star 趋势"></picture></a></p>

## 📄 许可证

[MIT](LICENSE) © 2026 AoyamaShou · zephyr4123 · nicocat
