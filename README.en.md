<div align="center">

[简体中文](README.md) · **English**

<img src="Assets/snugwin-logo.png" width="128" alt="SnugWin Icon">

# SnugWin

**Make Your Remote Windows Native on Any Device**

Display and touch enhancements for remote Windows PCs<br>A dedicated virtual screen for every device (up to 4) · Native touch gestures · App Library · In-App Auto Update

<p>Works with<br><a href="https://uuyc.163.com/"><img src="https://dl.zephyrxiang.com/snug/media/remote-uu.7b92201d.png" width="40" align="absmiddle" alt=""> <b>UU Remote</b></a>&emsp;&emsp;<a href="https://github.com/kunkundi/crossdesk"><img src="https://dl.zephyrxiang.com/snug/media/remote-crossdesk.0ffdb226.png" width="40" align="absmiddle" alt=""> <b>CrossDesk</b></a></p>

[![Latest Release](https://img.shields.io/github/v/release/nicocart/snug-win?label=Release&color=blue)](https://github.com/nicocart/snug-win/releases/latest) [![CI](https://github.com/nicocart/snug-win/actions/workflows/ci.yml/badge.svg)](https://github.com/nicocart/snug-win/actions/workflows/ci.yml) [![Release Workflow](https://github.com/nicocart/snug-win/actions/workflows/release.yml/badge.svg)](https://github.com/nicocart/snug-win/actions/workflows/release.yml) ![Windows 10/11 x64](https://img.shields.io/badge/Windows-10%20%2F%2011%20x64-0078D6) ![.NET 10 LTS](https://img.shields.io/badge/.NET-10%20LTS-512BD4) [![MIT License](https://img.shields.io/badge/License-MIT-green)](LICENSE)

[Download](#-download) · [Quick Start](#-quick-start) · [Native Experience](#-native-on-every-device) · [Every Device](#-one-windows-pc-every-device) · [App Library](#-app-library) · [Screenshots](#-screenshots--cropping) · [Comparison](#-compared-with-alternatives) · [Install & Update](#-install--update) · [Development](#-development)

<br>

<img src="https://dl.zephyrxiang.com/snug/media/hero-architecture.e4ceb0af.png" width="92%" alt="Diagram: SnugWin creates a dedicated virtual screen for each phone, tablet, and computer, delivered through a remote desktop app to fill each device">
<p><sub>▶ <a href="https://dl.zephyrxiang.com/snug/media/phone-demo.a97e46d1.mp4">Watch the HD demo video</a> (visual previews and demos are sourced from upstream <a href="https://github.com/zephyr4123/snug">Snug</a>; SnugWin replicates the same interaction experience on Windows)</sub></p>

</div>

> [!IMPORTANT]
> **New: CrossDesk**: [CrossDesk](https://github.com/kunkundi/crossdesk), a free and open-source remote desktop app, is now supported with the same virtual displays, native gestures, and App Library as UU Remote. See [Quick Start](#-quick-start).
>
> **For UU Remote VIP members**: from December 1, 2026, UU Remote moves multi-display and virtual display features to VIP. SnugWin works with UU Remote VIP as well as free CrossDesk; it is an independent community project with no commercial ties to UU Remote.

> Faithfully follows the product design, multi-screen & multi-remote adapter architecture, and interaction model of upstream macOS [**zephyr4123/snug**](https://github.com/zephyr4123/snug) (`v2.8.0`, MIT License), engineered natively for **Windows 10 / 11 + NetEase UU Remote / CrossDesk**.

---

## 📥 Download

<img src="Assets/snugwin-logo.png" width="88" align="left" alt="SnugWin Icon">

**SnugWin for Windows** [![Latest Release](https://img.shields.io/github/v/release/nicocart/snug-win?label=Release&color=blue)](https://github.com/nicocart/snug-win/releases/latest)<br>Supports Windows 10 (2004+) / Windows 11 x64, built on .NET 10 LTS + WPF<br>Built and verified automatically via GitHub Actions with SHA-256 checksums, [Go to Releases](https://github.com/nicocart/snug-win/releases/latest)

<br clear="left">

| Asset | Audience | Description |
| :--- | :--- | :--- |
| **`SnugWin-vX.Y.Z-win-x64-setup.exe`** | **Recommended (Installer)** | Inno Setup installer bundling .NET 10 runtime, verified driver setup, shortcuts, and optional autostart |
| `SnugWin-vX.Y.Z-win-x64-selfcontained.zip` | Portable (No Install) | Self-contained .NET 10 runtime + bundled `driver\`; unzip, one-click install driver in-app, and enjoy SHA-256 verified hot updates |
| `SnugWin-vX.Y.Z-win-x64-frameworkdependent.zip` | Minimal Size | Bundled `driver\`; requires [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0) |
| `virtual-display-driver-0.3.1-x64.zip` | Standalone Driver (Optional) | Standalone virtual display driver + `install-driver.cmd` (already bundled inside `setup.exe` and both portable zips) |
| `checksums.txt` | Integrity Check | SHA-256 hashes for all release assets |

> SnugWin runs only on the Windows host being controlled; the controlling phone, tablet, or computer needs only UU Remote or CrossDesk.

---

## 🚀 Quick Start

| Step | <img src="https://dl.zephyrxiang.com/snug/media/remote-uu.7b92201d.png" width="20" align="absmiddle" alt=""> UU Remote (GameViewer) | <img src="https://dl.zephyrxiang.com/snug/media/remote-crossdesk.0ffdb226.png" width="20" align="absmiddle" alt=""> CrossDesk |
| :--- | :--- | :--- |
| **1. Setup** | Launch SnugWin and [UU Remote](https://uuyc.163.com/) | Launch SnugWin, enable **Keep Screens Ready ("提前准备虚拟屏")** under **Settings → Multi-Screen**, then click **"一键重启远控" (One-Click Restart Remote)** in the Settings banner (or restart `CrossDeskService` and CrossDesk, since CrossDesk enumerates displays only at startup) |
| **2. Connect** | Connect from your phone; SnugWin creates your virtual screen automatically | Connect from your phone; pre-created screens are already ready in CrossDesk |
| **3. Pick Screen** | Select the Snug screen in UU Remote's display switcher | Select the Snug screen in CrossDesk's display switcher |
| **4. Touch Mode** | Works out of the box | Switch from **Relative Mouse** to **Absolute Mouse** in CrossDesk's mobile floating menu/settings to enable native single-finger scrolling & inertia (works even with CrossDesk's privacy input guard active) |
| **5. End / Dismiss** | Windows restore and non-resident virtual screens retire on disconnect | Prepared screens stay online (local cursor is repelled when idle) while windows return to the primary monitor; dismissing keeps the screen alive |

---

## ✨ Native on Every Device

Remote desktop apps scale your entire desktop into a tiny mobile viewport, leaving black bars and unreadable text. SnugWin provisions dedicated virtual displays (up to **4 configurable Phone / Tablet / Computer screens**, including resident screens) so Windows windows fill every device at true native scale.

### Phone: Portrait · Landscape

<p align="center"><img src="https://dl.zephyrxiang.com/snug/media/compare-uu-original.cf4158d1.png" width="36%" alt="Default remote: entire desktop shrunk onto a phone screen"> <img src="https://dl.zephyrxiang.com/snug/media/phone-claude-code.93e21d9d.png" width="36%" alt="Snug: window fills the screen with larger text"><br><sub>Same content · Left: Default remote · Right: Snug visual preview</sub></p>

* **Multi-Remote Session Sensing**: Multi-signal **UU Remote** detection (GameViewer overlay, UIA controller table, `GameViewerServer` streamer-log growth) and **CrossDesk** detection (`crossdesk.log` + `CrossDeskService` persistent host tracking) manage `virtual-display-rs` monitors via EDID ownership (`OwnedDisplayBook`).
* **DWM-Compensated Fit**: Placed windows snap cleanly edge-to-edge on the virtual screen without invisible drop-shadow margins.
* **Remembered Terminal/Editor Zoom**: Automatically steps font zoom up for terminals, VS Code, Cursor, etc., and restores zoom + original window frames on exit or disconnect.
* **Same-Session Re-entry**: Dismissing the phone screen during a session leaves a quick **"Enter Phone Screen"** pill (plus tray menu item) so you can bring it right back without reconnecting.

<p align="center"><img src="https://dl.zephyrxiang.com/snug/media/phone-landscape-web.a1670af8.png" width="72%" alt="Phone in landscape: wide page displayed in full"><br><sub>Landscape: wide pages in full</sub></p>

### Native Touch Gestures & Selection Toolbar

| Mobile Gesture | Default Remote | SnugWin Virtual Screen |
| :--- | :--- | :--- |
| Single-finger swipe | Mouse drag (selects text by mistake) | Smooth wheel scrolling with 60fps inertia |
| Hold 0.5s then drag | Immediate drag | Clean text selection (replays exact press origin) + **`Copy \| Paste \| Delete`** floating bar |
| Hold in place & release | Left click | Right-click context menu (can be disabled independently for PowerShell) |
| Browser tab strip tap | — | Passes taps straight through for instant Chrome / Edge / Firefox tab switching |
| Close window button | Closes in place | Restores window back to desktop before closing |

<p align="center"><img src="https://dl.zephyrxiang.com/snug/media/phone-demo.2cee4e81.webp" width="30%" alt="Live demo: opening a window, typing commands, one-finger scrolling, returning to library"> <img src="https://dl.zephyrxiang.com/snug/media/phone-context-menu.997dd70f.png" width="30%" alt="Long-press context menu"><br><sub>One-finger scrolling · Long-press context menu</sub></p>

> Selection toolbar shortcuts adapt to the target window: **Copy** uses `Ctrl+Shift+C` in terminals (`Ctrl+C` elsewhere); **Paste** uses `Ctrl+Shift+V` in Windows Terminal (`Ctrl+V` elsewhere); **Delete** sends `Backspace` without overwriting your clipboard. For **CrossDesk**, select **Absolute Mouse** mode on the mobile client.

### Tablet: Full Screen · Native Gestures

* **Landscape and portrait** per-model aspect ratios, full-screen touch gestures with no external mouse required, and a two-column App Library in landscape.

<p align="center"><img src="https://dl.zephyrxiang.com/snug/media/ipad-landscape-vscode.7524622f.png" width="56%" alt="Tablet in landscape: VS Code fills the screen with larger text"> <img src="https://dl.zephyrxiang.com/snug/media/ipad-portrait-safari.22fa3ccc.png" width="27%" alt="Tablet in portrait: browser fills the screen"><br><sub>Tablet · Landscape · Portrait</sub></p>

### Computer: Full Screen · No Black Bars

* Creates a dedicated virtual monitor matching the controlling computer's resolution so there are zero black bars, preserving native mouse/keyboard behavior while leaving physical monitors untouched.

<p align="center"><img src="https://dl.zephyrxiang.com/snug/media/macbook-uu-original.4c593493.png" width="46%" alt="Default remote on a laptop: mismatched aspect ratio with black bars"> <img src="https://dl.zephyrxiang.com/snug/media/macbook-snug.32bf15ab.png" width="46%" alt="Snug computer screen: same window fills the screen cleanly"><br><sub>Same window · Left: Default remote · Right: Snug computer screen</sub></p>

---

## 🧩 One Windows PC, Every Device

Configure up to **4 virtual screens** (Phone, Tablet, or Computer) under **Settings → Multi-Screen**, toggle each independently, mark a screen as **Resident ("设为常驻屏")**, or enable **Keep Screens Ready ("提前准备虚拟屏")** for launch-only remotes like CrossDesk.

<p align="center"><img src="https://dl.zephyrxiang.com/snug/media/uu-screen-list.a161e201.png" width="48%" alt="Multiple Snug screens listed in the remote display switcher"><br><sub>Multiple Snug virtual screens in the remote display picker</sub></p>

* Built-in `Assets/device-models.json` catalog covers **19 brands (~785 phones & 46 tablets)** plus common computer resolutions, alongside custom resolution/aspect input.

---

## 🗂 App Library

* **Running Apps**: Layered cards grouped by current vs other apps across all monitors/minimized states.
* **All Apps**: 4-column iOS-style grid extracting crisp **128×128** icons via Win32 `IShellItemImageFactory` for both Win32 EXEs and Microsoft Store UWP apps (`shell:AppsFolder`).
* **Pinyin Search**: Search by English name, window title, Chinese characters, full Pinyin, or Pinyin initials.

<p align="center"><img src="https://dl.zephyrxiang.com/snug/media/phone-app-library.865fc506.png" width="30%" alt="Running apps"> <img src="https://dl.zephyrxiang.com/snug/media/phone-all-apps.7d4ada60.png" width="30%" alt="All apps grid"> <img src="https://dl.zephyrxiang.com/snug/media/phone-search.d365e194.png" width="30%" alt="Pinyin search"><br><sub>Running · All Apps · Pinyin Initials Search</sub></p>

* **Two-Column Landscape & Computer Edge Bar**: Landscape phones/tablets show Apps and Windows side by side; computer screens offer a subtle left-edge handle to pull windows from other monitors.

<p align="center"><img src="https://dl.zephyrxiang.com/snug/media/phone-landscape-library.30220d6f.png" width="54%" alt="Phone landscape two-column library"> <img src="https://dl.zephyrxiang.com/snug/media/ipad-landscape-library.b0f8b829.png" width="39%" alt="Tablet landscape two-column library"><br><sub>Two-Column Library · Landscape Phone · Tablet</sub></p>

<p align="center"><img src="https://dl.zephyrxiang.com/snug/media/mac-library-panel.686cc617.png" width="38%" alt="Computer screen left-edge App Library panel"><br><sub>Computer Screen: Left-Edge App Library</sub></p>

* **Fit Any App**: Windows with minimum-width constraints automatically expand the virtual display along the exact same aspect ratio so nothing is ever clipped.

<p align="center"><img src="https://dl.zephyrxiang.com/snug/media/phone-chatgpt.a28ef053.png" width="36%" alt="Desktop app filling phone screen"> <img src="https://dl.zephyrxiang.com/snug/media/phone-fit-settings.4e526ad6.png" width="36%" alt="Minimum-width Settings window fitted cleanly"><br><sub>Left: Native desktop app · Right: Minimum-width window fitted cleanly</sub></p>

---

## 📸 Screenshots & Cropping

Enable the optional camera button on the bottom home bar to capture raw pixels of the active window straight into the Windows clipboard (unaffected by remote compression), then tap the corner thumbnail to crop with **8 handles**.

<p align="center"><img src="https://dl.zephyrxiang.com/snug/media/phone-screenshot-thumbnail.3dfb4e94.png" width="30%" alt="Screenshot thumbnail preview"> <img src="https://dl.zephyrxiang.com/snug/media/phone-screenshot-crop.0bd33902.png" width="30%" alt="8-handle screenshot crop overlay"><br><sub>Left: Thumbnail preview · Right: 8-handle crop</sub></p>

---

## 🆚 Compared with Alternatives

| Approach | Limitation | SnugWin |
| :--- | :--- | :--- |
| **AI Assistant Remote Control** | Locked to a single AI product or chat pane | Works with every native Windows app and terminal |
| **UU Remote Built-in Terminal** | Separate session detached from your local desktop | Controls your existing local terminals and IDEs seamlessly |
| **Dummy Plugs / Basic Virtual Displays** | Single aspect ratio; no touch gestures or window management | Per-device screens with native touch gestures, selection bar, and App Library |

---

## 📦 Install & Update

### Option 1: One-Click Installer (Recommended)
1. Download `SnugWin-vX.Y.Z-win-x64-setup.exe` from [Releases](https://github.com/nicocart/snug-win/releases/latest) and run it.
2. Keep **"Install or verify virtual-display-rs 0.3.1 driver"** checked (verifies SHA-256 & cert thumbprint, uses `INSTALLFLAG_FORCE` if an OEM virtual display driver shares `Root\VirtualDisplayDriver`, and records ownership in `%ProgramData%\SnugWin\driver-ownership.json` so pre-existing user drivers are never removed).
3. Optionally enable **"Launch SnugWin silently in tray when signing into Windows"**.

### Option 2: Portable Zip
1. Extract `SnugWin-vX.Y.Z-win-x64-selfcontained.zip` (the `driver\` folder is already bundled inside).
2. Launch `SnugWin.App.exe` (accept UAC prompt — elevated rights enable managing admin terminals/IDEs across UIPI and switching CCD display modes). If the virtual display driver is not yet installed, click **"Install Bundled Driver"** right in the top setup banner (or run `driver\install-driver.cmd` as Administrator).

### Silent Logon Autostart & Hot Updates
* **Logon Autostart**: Uses Windows Task Scheduler (`SnugWinAutoStart`, `HighestAvailable`, `--minimized`) so elevated startup runs silently without UAC prompts on every login.
* **In-App Updates**: Check GitHub Releases automatically or manually under **About & Updates**; downloads verify against `checksums.txt` SHA-256, cleanly restore open windows, replace binaries, and relaunch.

---

## 🔧 Development

* **Stack**: C# / `.NET 10 LTS` / WPF (`PerMonitorV2`) + `Win32 / UIAutomation / CCD / WH_MOUSE_LL` + `Inno Setup 6`
* **Architecture**: `SnugWin.Core` (platform-independent logic, multi-remote & touch state machines) → `SnugWin.Windows` (display, windowing, hooks, UU & CrossDesk sensing, startup/updater) → `SnugWin.App` (modern dark WPF UI & tray host)

```powershell
dotnet build SnugWin.sln -c Release -warnaserror
dotnet test SnugWin.sln -c Release --no-build
```

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for third-party attributions and licenses.

---

## 🙏 Acknowledgments

* [**NetEase UU Remote (GameViewer)**](https://uuyc.163.com/) for reliable low-latency remote access.
* [**zephyr4123/snug**](https://github.com/zephyr4123/snug) (MIT License · AoyamaShou, zephyr4123, and upstream contributors including Nexmoe, 李小双, 11ze, rangerTao) for original product design, multi-remote architecture, dual-screen icon concept, state machines, device model catalog, and preview media assets.
* [**CrossDesk**](https://github.com/kunkundi/crossdesk) (GPL-3.0) for free open-source remote access; SnugWin interacts strictly via observable behavior and contains none of its code.
* [**MolotovCherry/virtual-display-rs**](https://github.com/MolotovCherry/virtual-display-rs) (MIT License) for Windows IddCx virtual display driver & named-pipe IPC.
* **Contributors**: [@WilliamLambertCN](https://github.com/WilliamLambertCN) (PR #1), [@Jym2004](https://github.com/Jym2004) (PR #2), and [@orion-wh](https://github.com/orion-wh) (PR #3).

---

## ⭐ Star History

<p align="center"><a href="https://www.star-history.com/#nicocart/snug-win&Date"><picture><source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/svg?repos=nicocart/snug-win&type=Date&theme=dark"><source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/svg?repos=nicocart/snug-win&type=Date"><img src="https://api.star-history.com/svg?repos=nicocart/snug-win&type=Date" width="80%" alt="SnugWin GitHub Star History"></picture></a></p>

## 📄 License

[MIT](LICENSE) © 2026 AoyamaShou · zephyr4123 · nicocat
