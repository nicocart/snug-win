<div align="center">

[简体中文](README.md) · **English**

<img src="Assets/snugwin-logo.png" width="128" alt="SnugWin Icon">

# SnugWin

**Make Windows feel native on your phone and tablet**

Supports **NetEase UU Remote** · **CrossDesk**<br>Up to 4 Dedicated Virtual Displays (Phone / Tablet / Computer) · Native Touch Gestures · App Library · In-App Auto Update

[![Latest Release](https://img.shields.io/github/v/release/nicocart/snug-win?label=Release&color=blue)](https://github.com/nicocart/snug-win/releases/latest) [![CI](https://github.com/nicocart/snug-win/actions/workflows/ci.yml/badge.svg)](https://github.com/nicocart/snug-win/actions/workflows/ci.yml) [![Release Workflow](https://github.com/nicocart/snug-win/actions/workflows/release.yml/badge.svg)](https://github.com/nicocart/snug-win/actions/workflows/release.yml) ![Windows 10/11 x64](https://img.shields.io/badge/Windows-10%20%2F%2011%20x64-0078D6) ![.NET 10 LTS](https://img.shields.io/badge/.NET-10%20LTS-512BD4) [![MIT License](https://img.shields.io/badge/License-MIT-green)](LICENSE)

[Download](#-download) · [Quick Start](#-quick-start) · [Native Experience](#-native-experience) · [App Library](#-app-library) · [Install & Update](#-install--update) · [Development](#-development)

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

---

## 🚀 Quick Start

| Step | UU Remote (GameViewer) | CrossDesk |
| :--- | :--- | :--- |
| **1. Setup** | Launch SnugWin and UU Remote | Launch SnugWin, enable **Keep Screens Ready ("提前准备虚拟屏")** under **Settings → Multi-Screen**, then click **"一键重启远控" (One-Click Restart Remote)** in the Settings banner (or restart `CrossDeskService` and CrossDesk, since CrossDesk enumerates displays only at startup) |
| **2. Connect** | Connect from your phone; SnugWin creates your virtual screen automatically | Connect from your phone; pre-created screens are already ready in CrossDesk |
| **3. Pick Screen** | Select the Snug screen in UU Remote's display switcher | Select the Snug screen in CrossDesk's display switcher |
| **4. Touch Mode** | Works out of the box | Switch from **Relative Mouse** to **Absolute Mouse** in CrossDesk's mobile floating menu/settings to enable native single-finger scrolling & inertia (works even with CrossDesk's privacy input guard active) |
| **5. End / Dismiss** | Windows restore and non-resident virtual screens retire on disconnect | Prepared screens stay online (local cursor is repelled when idle) while windows return to the primary monitor; dismissing keeps the screen alive |

---

## ✨ Native Experience

Remote desktop apps scale your entire desktop into a tiny mobile viewport. SnugWin provisions dedicated virtual displays (up to **4 configurable Phone / Tablet / Computer screens**, including resident screens) so Windows windows fill your device at true native scale.

### Multi-Screen Display & Window Fitting

* **Multi-Remote Session Sensing**: Multi-signal **UU Remote** detection (GameViewer overlay, UIA controller table, `GameViewerServer` streamer-log growth) and **CrossDesk** detection (`crossdesk.log` + `CrossDeskService` persistent host tracking) manage `virtual-display-rs` monitors via EDID ownership (`OwnedDisplayBook`).
* **Up to 4 Virtual Screens & Keep Ready**: Configure Phone, Tablet, and Computer screens under **Settings → Multi-Screen**, mark a screen as **Resident ("设为常驻屏")**, or enable **Keep Screens Ready ("提前准备虚拟屏")** for launch-only remotes like CrossDesk.
* **DWM-Compensated Fit**: Placed windows snap cleanly edge-to-edge on the virtual screen without invisible drop-shadow margins.
* **Remembered Terminal/Editor Zoom**: Automatically steps font zoom up for terminals, VS Code, Cursor, etc., and restores zoom + original window frames on exit or disconnect.
* **Same-Session Re-entry**: Dismissing the phone screen during a session leaves a quick **"Enter Phone Screen"** pill (plus tray menu item) so you can bring it right back without reconnecting.

### Native Touch Gestures & Selection Toolbar

| Mobile Gesture | Default Remote | SnugWin Virtual Screen |
| :--- | :--- | :--- |
| Single-finger swipe | Mouse drag (selects text by mistake) | Smooth wheel scrolling with 60fps inertia |
| Hold 0.5s then drag | Immediate drag | Clean text selection (replays exact press origin) + **`Copy \| Paste \| Delete`** floating bar |
| Hold in place & release | Left click | Right-click context menu (can be disabled independently for PowerShell) |
| Browser tab strip tap | — | Passes taps straight through for instant Chrome / Edge / Firefox tab switching |
| Close window button | Closes in place | Restores window back to desktop before closing |

> Selection toolbar shortcuts adapt to the target window: **Copy** uses `Ctrl+Shift+C` in terminals (`Ctrl+C` elsewhere); **Paste** uses `Ctrl+Shift+V` in Windows Terminal (`Ctrl+V` elsewhere); **Delete** sends `Backspace` without overwriting your clipboard.

### Screenshots & 8-Handle Cropping

Enable the optional camera button on the bottom home bar to capture raw pixels of the active window straight into the Windows clipboard, then tap the corner thumbnail to crop with 8 handles.

---

## 📱 App Library

* **Running Apps**: Layered cards grouped by current vs other apps across all monitors/minimized states.
* **All Apps**: 4-column iOS-style grid extracting crisp **128×128** icons via Win32 `IShellItemImageFactory` for both Win32 EXEs and Microsoft Store UWP apps (`shell:AppsFolder`).
* **Pinyin Search**: Search by English name, window title, Chinese characters, full Pinyin, or Pinyin initials.
* **Universal Ratio Catalog**: Built-in `Assets/phone-models.json` (~800 phone/tablet profiles across 17 brands) plus custom resolution/aspect input.

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
* [**zephyr4123/snug**](https://github.com/zephyr4123/snug) (MIT License · AoyamaShou, zephyr4123, and upstream contributors including Nexmoe, 李小双, 11ze) for original product design, multi-remote architecture, dual-screen icon concept, state machines, and device model catalog.
* [**CrossDesk**](https://github.com/kunkundi/crossdesk) (GPL-3.0) for free open-source remote access; SnugWin interacts strictly via observable behavior and contains none of its code.
* [**MolotovCherry/virtual-display-rs**](https://github.com/MolotovCherry/virtual-display-rs) (MIT License) for Windows IddCx virtual display driver & named-pipe IPC.
* **Contributors**: [@WilliamLambertCN](https://github.com/WilliamLambertCN) (PR #1), [@Jym2004](https://github.com/Jym2004) (PR #2), and [@orion-wh](https://github.com/orion-wh) (PR #3).

## 📄 License

[MIT](LICENSE) © 2026 AoyamaShou · zephyr4123 · nicocat
