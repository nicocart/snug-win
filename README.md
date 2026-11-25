<div align="center">

**简体中文** · [English](README.en.md)

<img src="Assets/snugwin-logo.png" width="128" alt="SnugWin 图标">

# SnugWin

**Make Your Remote Windows Native on Any Device**

远程 Windows 的显示与触控增强<br>每台设备专属屏幕（最多 4 块） · 原生触屏手势 · App 资源库 · 应用内自动更新

<p>已支持的远控<br><a href="https://uuyc.163.com/"><img src="https://dl.zephyrxiang.com/snug/media/remote-uu.7b92201d.png" width="40" align="absmiddle" alt=""> <b>UU远程</b></a>&emsp;&emsp;<a href="https://github.com/kunkundi/crossdesk"><img src="https://dl.zephyrxiang.com/snug/media/remote-crossdesk.0ffdb226.png" width="40" align="absmiddle" alt=""> <b>CrossDesk</b></a></p>

[![最新版本](https://img.shields.io/github/v/release/nicocart/snug-win?label=%E6%9C%80%E6%96%B0%E7%89%88%E6%9C%AC&color=blue)](https://github.com/nicocart/snug-win/releases/latest) [![CI](https://github.com/nicocart/snug-win/actions/workflows/ci.yml/badge.svg)](https://github.com/nicocart/snug-win/actions/workflows/ci.yml) [![Release](https://github.com/nicocart/snug-win/actions/workflows/release.yml/badge.svg)](https://github.com/nicocart/snug-win/actions/workflows/release.yml) ![Windows 10/11 x64](https://img.shields.io/badge/Windows-10%20%2F%2011%20x64-0078D6) ![.NET 10 LTS](https://img.shields.io/badge/.NET-10%20LTS-512BD4) [![许可证 MIT](https://img.shields.io/badge/%E8%AE%B8%E5%8F%AF%E8%AF%81-MIT-green)](LICENSE)

[下载](#-下载) · [快速上手](#-快速上手) · [原生体验](#-每台设备原生体验) · [多设备](#-一台-windows所有设备) · [App 资源库](#-app-资源库) · [截图](#-截图与裁剪) · [对比](#-与其他方案的对比) · [安装与更新](#-安装与更新) · [使用](#-使用方式) · [限制](#-已知限制) · [开发](#-开发)

<br>

<img src="https://dl.zephyrxiang.com/snug/media/hero-architecture.e4ceb0af.png" width="92%" alt="原理图：SnugWin 为手机、平板、电脑分别建立专属虚拟屏幕，经远控软件推送至各设备并铺满画面">
<p><sub>▶ <a href="https://dl.zephyrxiang.com/snug/media/phone-demo.a97e46d1.mp4">观看高清演示视频</a>（本页示意效果图与动图取自上游 <a href="https://github.com/zephyr4123/snug">Snug</a>，SnugWin 在 Windows 上 1:1 复刻交互体验）</sub></p>

</div>

> [!IMPORTANT]
> **新增 CrossDesk**：已支持免费、开源的远控软件 [CrossDesk](https://github.com/kunkundi/crossdesk)，屏幕、原生手势与 App 资源库同 UU远程；首次使用参见[快速上手](#-快速上手)。
>
> **致 UU远程 VIP 用户**：UU远程自 2026 年 12 月 1 日起将多屏、虚拟屏等功能纳入 VIP。SnugWin 基于虚拟屏架构，若你恰为 UU远程 VIP 用户，欢迎一试；若需免费方案，可搭配 CrossDesk。
>
> **立场**：SnugWin 为独立社区项目，与 UU远程 无商业关联。是否开通 VIP，请依自身对 UU远程 的需求决定，不必为 SnugWin 专门开通。对接部分为可替换的适配层，欢迎提 PR 接入其他远控或 fork 自行适配，详见[开发](#-开发)。

> 1:1 承接并跟随上游 macOS 原版 [**zephyr4123/snug**](https://github.com/zephyr4123/snug)（MIT License，`v2.9.0`）的产品体验、多屏多远控适配与交互状态机，专为 **Windows 10 / 11 + 网易 UU远程 / CrossDesk** 打造。

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

> SnugWin 仅需安装于被控 Windows 电脑；控制端（iPhone、安卓手机、平板或另一台电脑）只需安装 UU远程 或 CrossDesk，无需额外安装任何 App。

---

## 🚀 快速上手

按所用远控操作；首次设置后，日常直接连接即可。

| 步骤 | <img src="https://dl.zephyrxiang.com/snug/media/remote-uu.7b92201d.png" width="20" align="absmiddle" alt=""> UU远程（GameViewer） | <img src="https://dl.zephyrxiang.com/snug/media/remote-crossdesk.0ffdb226.png" width="20" align="absmiddle" alt=""> CrossDesk |
| :--- | :--- | :--- |
| **1. 远控** | Windows 与手机均安装 [UU远程](https://uuyc.163.com/) | Windows 与手机均安装 [CrossDesk](https://github.com/kunkundi/crossdesk) |
| **2. 启动与驱动** | 安装并运行 SnugWin（首次若提示缺驱动，点击顶部「立即一键安装驱动」） | 同左 |
| **3. 准备屏幕** | 无需操作，连接后自动建立虚拟屏 | 在 **设置 → 多屏管理** 开启 **「提前准备虚拟屏」**，再点击设置页顶部 **「一键重启远控」**（重启 `CrossDeskService` 与 CrossDesk 以识别新屏） |
| **4. 手机模式** | 开箱即用，无需切换 | 在手机端悬浮菜单/设置中将 **「相对鼠标」切换为「绝对鼠标」**（支持 CrossDesk 隐私屏） |
| **5. 连接切屏** | 连接 Windows，在屏幕列表中选择「Snug 手机屏」 | 连接 Windows，在悬浮菜单「显示器」中选择「Snug 手机屏」 |
| **6. 结束 / 退出** | 断开或「退出手机屏」后窗口归位并撤除虚拟屏（设为「常驻」除外） | 断开后自动把窗口归位并保留提前准备的屏幕（空闲时防误滑入）；手机端「退出手机屏」仅还原窗口、保留屏幕 |

- **平板、电脑或横屏手机**：先在 **设置 → 多屏管理** 添加对应屏幕，连接后在远控中选择该屏
- **CrossDesk 何时再重启**：仅在新增/改比例虚拟屏，或开机后 CrossDesk 先于 SnugWin 启动时，点击设置页 **「一键重启远控」** 即可

---

## ✨ 每台设备，原生体验

远程控制软件默认将整块桌面等比压缩进控制端屏幕，比例不符即留黑边，文字与控件随之缩小数倍。SnugWin 为每台控制端建立一块比例匹配的专属虚拟屏：手机与平板按移动端尺度铺满并配原生触控手势；电脑按控制端分辨率全屏铺满无黑边。

### 手机：竖屏 · 横屏

<p align="center"><img src="https://dl.zephyrxiang.com/snug/media/compare-uu-original.cf4158d1.png" width="36%" alt="原版远控：整块桌面缩小显示于手机屏幕"> <img src="https://dl.zephyrxiang.com/snug/media/phone-claude-code.93e21d9d.png" width="36%" alt="Snug：窗口铺满全屏，字号放大"><br><sub>同一内容 · 左：原版远控 · 右：Snug 效果示意</sub></p>

* **竖屏、横屏**：按机型计算比例并经 Windows CCD 高 DPI 呈现；横屏适合代码、终端与宽幅页面
* **专属虚拟屏**：支持 **UU远程** 四路探测（GameViewer 覆盖窗、UIA 控制器列表、`GameViewerServer` 推流日志增长与 TCP 辅助诊断）与 **CrossDesk**（支持 `CrossDeskService` 持久宿主日志识别与进程存活检测）；通过 `virtual-display-rs` 按 SnugWin 归属 ID 隔离管理，只清理自身虚拟屏
* **窗口铺满**：选中的窗口通过 DWM 扩展边框补偿精确贴满手机屏
* **字号放大**：终端、VS Code、Cursor 等编辑器自动放大字号，按 App 分别记忆，并在恢复原位时自动缩回
* **同会话重进手机屏**：在手机端退出手机屏后，本次会话期间会在主屏右下角保留轻量化 **「进入手机屏」** 悬浮胶囊并在托盘提供重进入口，随时一键恢复竖屏

<p align="center"><img src="https://dl.zephyrxiang.com/snug/media/phone-landscape-web.a1670af8.png" width="72%" alt="手机横屏：宽幅网页完整呈现"><br><sub>横屏：宽幅页面完整呈现</sub></p>

### 原生手势与悬浮操作条

远程客户端手机端常沿用触控板鼠标逻辑；SnugWin 通过独立高优先级 `WH_MOUSE_LL` 钩子 + 异步重放队列（兼容 CrossDesk 隐私输入守卫标记透传），在手机屏与平板屏内按触屏习惯重新映射：

| 手机上的操作 | 原版远控 | SnugWin 虚拟屏 |
| :--- | :--- | :--- |
| 单指滑动 | 鼠标拖拽，容易误选文字 | 页面平滑滚动，松手带 60fps 惯性 |
| 长按 0.5 秒后拖动 | 鼠标拖拽 | 精确还原按压原点划选文字；松手弹出 **「复制 \| 粘贴 \| 删除」** 悬浮操作条 |
| 原地长按松开 | 普通左键点击 | 调出右键菜单（可在设置中单独关闭，避免 PowerShell 等终端误触发右键粘贴） |
| 浏览器标签栏点击 | — | Edge / Chrome / Firefox 等原生标签页区域直通点击，零延迟切换 |
| 仅接管注入事件 | — | 默认只翻译远控注入的触控，本机物理鼠标跨入虚拟屏仍保持原生操控（可在设置切换） |

<p align="center"><img src="https://dl.zephyrxiang.com/snug/media/phone-demo.2cee4e81.webp" width="30%" alt="实机演示：打开窗口、弹出键盘输入命令、单指滚动、返回列表"> <img src="https://dl.zephyrxiang.com/snug/media/phone-context-menu.997dd70f.png" width="30%" alt="长按调出右键菜单"><br><sub>单指滚动 · 长按调出右键菜单</sub></p>

> 划选悬浮操作条会自动识别当前窗口类型：**复制**在终端自动发送 `Ctrl+Shift+C`、普通窗口发送 `Ctrl+C`；**粘贴**在 Windows Terminal 自动发送 `Ctrl+Shift+V`、其他应用发送 `Ctrl+V`；**删除**发送 `Backspace` 清除可编辑选区而不覆盖剪贴板。**CrossDesk** 手机端须选「绝对鼠标」以启用触控手势翻译。

### 平板：铺满 · 原生手势

平板远程 Windows 以往多需外接键盘与鼠标；SnugWin 平板屏按机型铺满，触控即用。

* **横屏、竖屏**：按机型计算比例，全屏铺满
* **原生手势**：同手机屏，滚动、右键、拖选均由触控完成，无需外接鼠标
* **左右两栏**：横屏时 App 资源库自动切换为左右两栏，左侧 App 列表、右侧窗口列表

<p align="center"><img src="https://dl.zephyrxiang.com/snug/media/ipad-landscape-vscode.7524622f.png" width="56%" alt="平板横屏：VS Code 铺满全屏，字号放大"> <img src="https://dl.zephyrxiang.com/snug/media/ipad-portrait-safari.22fa3ccc.png" width="27%" alt="平板竖屏：网页铺满全屏"><br><sub>平板 · 横屏 · 竖屏</sub></p>

### 电脑：铺满 · 无黑边

SnugWin 电脑屏按控制端分辨率建立，画面全屏无黑边，且不干扰原有物理显示器。

<p align="center"><img src="https://dl.zephyrxiang.com/snug/media/macbook-uu-original.4c593493.png" width="46%" alt="原版远控：不同比例屏幕上下留黑边，文字缩小"> <img src="https://dl.zephyrxiang.com/snug/media/macbook-snug.32bf15ab.png" width="46%" alt="Snug 电脑屏：同一窗口铺满无黑边"><br><sub>同一窗口 · 左：原版远控 · 右：Snug 电脑屏</sub></p>

* **分辨率一致**：内置常见电脑与笔记本分辨率，也支持自定义分辨率
* **保留电脑操作**：不转换触屏手势、不放大字号，鼠标与键盘保持原生桌面习惯
* **App 资源库**：屏幕左边缘小竖条随时呼出，一键切换或把其他屏幕的窗口搬到当前电脑屏

---

## 🧩 一台 Windows，所有设备

每块虚拟屏的设备类型、机型型号、方向与名称均可自定义，最多支持 **4 块** 并独立开关。

| 屏幕 | 比例与尺寸 | 屏上功能 |
| :--- | :--- | :--- |
| 手机 · 竖 / 横 | 按机型计算比例，高 DPI 铺满 | App 资源库、底部指示条、原生手势、悬浮操作条、字号放大 |
| 平板 · 横 / 竖 | 按机型计算比例 | 同上；横屏为左右两栏布局 |
| 电脑 · 横 | 与控制端分辨率一致，无黑边 | 左边缘小竖条 App 资源库，保留原生键鼠操作 |

<p align="center"><img src="https://dl.zephyrxiang.com/snug/media/uu-screen-list.a161e201.png" width="48%" alt="远控屏幕菜单中的多块 Snug 虚拟屏"><br><sub>远控屏幕列表中的多块 Snug 虚拟屏示意</sub></p>

* **一机两向 / 多机轮换 / 跨设备**：同一台手机可设竖屏与横屏各一块（支持一键添加另一方向），也可手机、平板、电脑各设一块按需切换
* **设为常驻屏 / 提前准备虚拟屏**：可将任意虚拟屏设为常驻，或开启「提前准备虚拟屏」配合 CrossDesk 等启动时认屏的远控
* **全机型比例库**：内置 `Assets/device-models.json`（覆盖 19 个品牌、约 785 款手机与 46 款平板，以及常见电脑分辨率），同时支持任意自定义分辨率与宽高比

---

## 🗂 App 资源库

手机屏与平板屏的首页专为指尖操作设计（1:1 复刻 iOS 资源库交互），查找、打开、切换、新建窗口、关闭或退出应用无需回到电脑前：

* **运行中**：按「当前 App / 其他 App」分层卡片展示，窗口无论在主屏、副屏还是最小化均可直接拉上手机屏
* **全部**：4 列应用图标网格，使用 Win32 `IShellItemImageFactory` 提取 **128×128** 高清透明图标，同时扫描传统桌面程序与 `shell:AppsFolder` 商店/UWP 应用（如 Windows Terminal、设置等）
* **实时搜索**：匹配应用名与窗口标题，支持中英文、拼音全拼、常见 App 多音字扩展及首字母（如输入 `blbl` 找到「哔哩哔哩」、`bwl` 找到「备忘录」）

<p align="center"><img src="https://dl.zephyrxiang.com/snug/media/phone-app-library.865fc506.png" width="30%" alt="运行中：手机屏上窗口所属的 App 置于最上方"> <img src="https://dl.zephyrxiang.com/snug/media/phone-all-apps.7d4ada60.png" width="30%" alt="全部：本机全部 App 的图标网格"> <img src="https://dl.zephyrxiang.com/snug/media/phone-search.d365e194.png" width="30%" alt="搜索：输入 bwl 找到备忘录"><br><sub>运行中 · 全部 · 拼音首字母搜索</sub></p>

* **左右两栏**：横屏手机与平板上，左栏为 App，右栏为所选 App 的窗口

<p align="center"><img src="https://dl.zephyrxiang.com/snug/media/phone-landscape-library.30220d6f.png" width="54%" alt="横屏手机：App 资源库左栏为 App，右栏为所选 App 的窗口"> <img src="https://dl.zephyrxiang.com/snug/media/ipad-landscape-library.b0f8b829.png" width="39%" alt="平板横屏：App 资源库左栏为 App，右栏为所选 App 的窗口"><br><sub>左右两栏 · 横屏手机 · 平板</sub></p>

* **电脑屏侧边栏**：远程时屏幕左边缘保留小竖条，轻点呼出 App 资源库，一键将其他屏幕上的窗口移至当前屏或还原

<p align="center"><img src="https://dl.zephyrxiang.com/snug/media/mac-library-panel.686cc617.png" width="38%" alt="电脑屏 App 资源库：左边缘小竖条打开，一键切换、移至此屏、还原"><br><sub>电脑屏：左边缘小竖条打开 App 资源库</sub></p>

| 操作 | 方式 |
| :--- | :--- |
| 打开窗口 | 轻点窗口卡片即铺满手机屏；轻点 App 激活或拉起最近窗口 |
| 新建窗口 | 轻点 App 卡片右上角「新建窗口」，新窗口直接按手机屏宽度打开 |
| 关闭 / 退出 | 轻点关闭或退出后需再点一次确认，防误触；10 秒未退出时支持强制结束 |
| 退出手机屏 | 轻点右上角「退出手机屏」并确认，窗口归位、手机屏撤除（CrossDesk 下还原窗口并保留屏幕） |

### 与 iPhone 一致的操作

| iPhone | SnugWin 手机屏 / 平板屏 |
| :--- | :--- |
| App 资源库 | 首页：查找、打开与管理电脑上的桌面与 UWP 应用 |
| 底部小横条 | 点击或单指上滑，随时回到 App 资源库 |
| 左上角「‹ 返回」 | 回到当前放在手机屏上的窗口；点击置顶窗口关闭按钮时先归位再关闭 |

### 适配任意 App

对限定最小宽度的应用（如部分 IDE、设置窗），SnugWin 会先尝试手机宽度；若窗口拒绝再窄，则自动把虚拟屏切换到恰好容纳它的同比例档位再铺满，确保内容不裁切、坐标点按准确。

<p align="center"><img src="https://dl.zephyrxiang.com/snug/media/phone-chatgpt.a28ef053.png" width="36%" alt="AI 桌面应用铺满 Snug 手机屏"> <img src="https://dl.zephyrxiang.com/snug/media/phone-fit-settings.4e526ad6.png" width="36%" alt="设有最小宽度的系统设置完整铺满手机屏"><br><sub>左：任意原生桌面窗口 · 右：设有最小宽度的设置窗仍完整铺满</sub></p>

---

## 📸 截图与裁剪

适用于手机屏与平板屏。在 SnugWin 设置中开启「相机键」后，底部小横条左侧出现相机图标；轻点即截取当前窗口原画写入 Windows 剪贴板（不受远控压缩画质影响），并在左下角浮出缩略图预览；轻点缩略图可进入 **8 把手裁剪模式**，以裁剪区域替换剪贴板内容。

<p align="center"><img src="https://dl.zephyrxiang.com/snug/media/phone-screenshot-thumbnail.3dfb4e94.png" width="30%" alt="截图后左下角显示缩略图"> <img src="https://dl.zephyrxiang.com/snug/media/phone-screenshot-crop.0bd33902.png" width="30%" alt="裁剪：拖动四角或四边收窄选区"><br><sub>左：截图后显示缩略图 · 右：轻点进入 8 把手裁剪</sub></p>

---

## 🆚 与其他方案的对比

| 方案 | 局限 | SnugWin |
| :--- | :--- | :--- |
| **AI 助手自带远程控制** | 仅绑定单一 AI 产品或仅限聊天框 | 直接操作电脑原生任意窗口（VS Code、终端、浏览器、微信等全部可用） |
| **UU远程自带扩展终端** | 独立于本机现有终端会话 | 直接操控本机已经打开的终端与开发窗口，工作流无缝接续 |
| **虚拟显示器工具 / 显卡欺骗器** | 单一比例、换设备留黑边；无触屏手势与窗口管理 | 每台设备专属屏幕；手机与平板自带原生手势、悬浮操作条与 App 资源库 |

---

## 📦 安装与更新

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

> 💡 建议在远控端将画质调至最高：画质较低时画面模糊，平板上尤为明显。

**固定竖屏像素（可选）**：选好「小米 15 Ultra」并退出 SnugWin，在 `%LocalAppData%\SnugWin\config.json` 的 `Screens` 数组中，仅给该手机竖屏条目加上 `"fixedNativePixels": true`，保存后重启。Windows 虚拟屏只请求 **1440 × 3200**，不随窗口内容改宽；删除该项或设为 `false` 即恢复默认适配。此选项不设置远控的传输分辨率或码率。

固定模式仅适用于手机竖屏：宽 480–2160、高 800–4320 像素，宽高均为偶数，长边不超过短边的 3 倍。仅填比例（如 9 × 20）或超出固定模式范围时仍按默认策略适配；缺少宽高或宽高无效的条目不建屏。

---

## 🔒 隔离与还原

* **只改动虚拟屏**：不修改主显示器分辨率与缩放，未切到虚拟屏时与原版远控完全一致。
* **先记录，后搬窗**：所有窗口的原位置与缩放级数持久化保存在 `%LocalAppData%\SnugWin\phone-screen.json` 与 `left-behind.json`，即使断电或异常退出，下次启动也能自动恢复。
* **不上传任何隐私数据**：除手动或可选开机检查 GitHub Release 新版本外，不连接任何第三方服务器；日志与配置全部仅保存在本地 `%LocalAppData%\SnugWin\`，截图仅写入本机剪贴板不留存。

---

## 🚧 已知限制

| 限制 | 说明 |
| :--- | :--- |
| 依赖虚拟屏驱动管道 | 需要系统存在 `virtual-display-rs` 命名管道 `\\.\pipe\virtualdisplaydriver`；如未就绪，可在设置页「驱动与日志」点击「安装 / 修复驱动」 |
| 需要管理员权限 | 为跨 UIPI 操控高权限窗口、设置 CCD 显示模式、重启远控服务及接管注入触控，清单需 `requireAdministrator`；正因如此开机自启依赖任务计划而非传统 `HKCU\Run` |
| 多屏与设备并发 | 支持最多同时启用 **4 块虚拟屏**（手机、平板、电脑混合配置）；同一时刻由当前主连接远控接管 |
| CrossDesk 启动认屏 | CrossDesk 仅在启动时枚举显示器，须开启「提前准备虚拟屏」并在新增/改比例后点一次「一键重启远控」 |
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

* **接入其他远控**：`SnugWin.Core` 的多远控适配层已解耦，欢迎提 PR 接入其他远控软件或 fork 自行适配。
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

* [**zephyr4123/snug**](https://github.com/zephyr4123/snug)（MIT License）：AoyamaShou 与 zephyr4123，提供全部产品理念、核心状态机模型、多远控架构（含上游贡献者 Nexmoe 的 CrossDesk 初版适配及 李小双、11ze、rangerTao 等社区改进）、双屏叠片图标设计、机型库数据集及本页演示素材；
* [**CrossDesk**](https://github.com/kunkundi/crossdesk)（GPL-3.0）：kunkundi，免费开源跨平台远控软件；SnugWin 仅按其公开日志与窗口/输入行为对接，不含其代码；
* [**MolotovCherry/virtual-display-rs**](https://github.com/MolotovCherry/virtual-display-rs)（MIT License）：Windows IddCx 虚拟显示器驱动与命名管道服务；
* **社区贡献者**：感谢 [@WilliamLambertCN](https://github.com/WilliamLambertCN)（PR #1：EDID 虚拟屏身份绑定、IPC 加固、固定竖屏像素与会话采样稳定性）、[@Jym2004](https://github.com/Jym2004)（PR #2：UIA 控制端读取与离屏唤醒、桌面端筛选、虚拟屏离线恢复与超级屏让路）与 [@orion-wh](https://github.com/orion-wh)（PR #3：显示拓扑竞态与幽灵容器冲突修复、连续失败熔断器、modeset 节流与开机防闪烁）。

---

## ⭐ Star 趋势

<p align="center"><a href="https://www.star-history.com/#nicocart/snug-win&Date"><picture><source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/svg?repos=nicocart/snug-win&type=Date&theme=dark"><source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/svg?repos=nicocart/snug-win&type=Date"><img src="https://api.star-history.com/svg?repos=nicocart/snug-win&type=Date" width="80%" alt="SnugWin 的 GitHub Star 趋势"></picture></a></p>

## 📄 许可证

[MIT](LICENSE) © 2026 AoyamaShou · zephyr4123 · nicocat
