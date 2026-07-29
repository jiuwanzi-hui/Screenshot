# SnapCut · 简截

[简体中文](#简体中文) | [English](#english)

[![GitHub release](https://img.shields.io/github/v/release/jiuwanzi-hui/Screenshot?style=flat-square&label=release)](https://github.com/jiuwanzi-hui/Screenshot/releases/latest)
[![GitHub downloads](https://img.shields.io/github/downloads/jiuwanzi-hui/Screenshot/total?style=flat-square&label=downloads)](https://github.com/jiuwanzi-hui/Screenshot/releases)
[![GitHub issues](https://img.shields.io/github/issues/jiuwanzi-hui/Screenshot?style=flat-square)](https://github.com/jiuwanzi-hui/Screenshot/issues)
[![GitHub stars](https://img.shields.io/github/stars/jiuwanzi-hui/Screenshot?style=flat-square)](https://github.com/jiuwanzi-hui/Screenshot/stargazers)
[![Windows 10 | 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?style=flat-square&logo=windows11)](https://www.microsoft.com/windows)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/)
[![Gitee mirror](https://img.shields.io/badge/Gitee-国内镜像-C71D23?style=flat-square&logo=gitee)](https://gitee.com/wwangyunhui/screenshot)

![SnapCut icon](src/Screenshot.App/Assets/Screenshot.png)

## 简体中文

SnapCut（简截）是一款面向 Windows 10/11 的轻量截图工具，取"简洁"与"直截了当"之意。它把区域截图、长截图、标注、OCR、钉图和可选翻译放在同一条紧凑工作流中，并优先在本机完成处理。

### 主要优点

- **菜单、下拉框和悬浮提示也能截到**：快捷键触发时先冻结当前桌面画面，尽量保留右键菜单、通知区域菜单、下拉列表、悬浮提示等容易因失去焦点而消失的临时界面，再进入框选和编辑。
- **截图后直接调整和编辑**：鼠标悬停到其他软件时可自动吸附整个窗口，单击直接选取，也可继续拖动自由框选；移动选区时底图保持固定，框内明亮、框外变暗。编辑后仍可用八个控制点扩展或缩小选区，边缘不会越过已有标注；已经放置的矩形、箭头、文字和表情可直接点击选中，再调整位置、大小、端点或边角，无需重新选择工具；并提供画笔、马赛克、颜色、线宽、撤销和重做。
- **受控长截图**：普通截图框选后点击工具栏的 `↕` 图标；单击选区可匀速向下采集，等待开始时双击则可先向上采集。程序发送固定节拍的细粒度鼠标滚轮消息，鼠标指针可自由移动且不会被锁定。单击暂停会先停稳并补齐最后视口，再允许继续；第一方向内双击会停稳后快速返回初始位置且不重复写入，随后自动向相反方向匀速拼接。到达真实边界后只停止滚动并保留当前结果，等待用户编辑、确认或取消；实时预览持续显示完整结果。
- **本地 OCR 与图片文字选择**：使用 Windows 本地 OCR 引擎，图片不需要上传；识别后可直接在截图、OCR 结果窗口和钉图上拖选文字并通过原生 Unicode 剪贴板复制，译文覆盖完成后同样可以选择和复制，识别语言取决于系统已安装的语言包。
- **可控的在线翻译**：截图工具栏可自动检测原文语言、批量翻译 OCR 文字并按原行位置覆盖到图片，复制和保存会包含译文，整组译文可一步撤销；支持不同厂家的 OpenAI 兼容接口，配置服务地址和 API Key 后可主动获取该厂家的模型列表，也可手动输入模型标识。服务原样返回原文时会明确提示而不生成假译文。只有用户明确启用并点击翻译时才发送文字，API Key 使用当前用户 DPAPI 加密保存。
- **高效的日常操作**：支持全局快捷键、系统托盘、截图历史、钉图、开机启动、深浅色主题，以及关闭窗口时最小化到后台或彻底退出；设置窗口每次从小窗、通知区域或快捷键重新显示到前台时都会检测更新，有新版本会在“版本更新”入口显示提示，并发检测 Gitee 国内源和 GitHub 国际源，自动采用先返回的有效更新源，下载失败时切换备用源；安装版和免安装版覆盖更新时都会保留 `ScreenshotData`。首次运行默认同时显示任务栏图标和通知区域图标，避免用户找不到程序入口。
- **数据跟随程序目录**：安装版与免安装版都把设置、加密凭据、历史和默认截图保存在 `SnapCut.exe` 旁的 `ScreenshotData`，不会把个人配置提交到源码仓库。

### 下载与安装

下载最新的 x64 自包含版本（发布文件名暂沿用 Screenshot- 前缀，以兼容旧版本的程序内自动更新；包内程序为 SnapCut.exe）：

- [安装版 SnapCut Setup 2.2.1（GitHub）](https://github.com/jiuwanzi-hui/Screenshot/releases/latest/download/Screenshot-Setup-2.2.1-win-x64.exe)
- [免安装版 SnapCut Portable 2.2.1（GitHub）](https://github.com/jiuwanzi-hui/Screenshot/releases/latest/download/Screenshot-Portable-2.2.1-win-x64.zip)
- [安装版 SnapCut Setup 2.2.1（Gitee 国内镜像）](https://gitee.com/wwangyunhui/screenshot/releases/download/v2.2.1/Screenshot-Setup-2.2.1-win-x64.exe)
- [免安装版 SnapCut Portable 2.2.1（Gitee 国内镜像）](https://gitee.com/wwangyunhui/screenshot/releases/download/v2.2.1/Screenshot-Portable-2.2.1-win-x64.zip)

免安装版解压后直接运行 `SnapCut.exe`，不需要安装或预先配置 .NET。安装版和免安装版的数据都保存在各自程序目录的 `ScreenshotData` 中；移动或卸载程序前请按需备份该目录。2.2.1 延续现有数据布局，并会在发现 1.0.0 的 `%LocalAppData%\Screenshot` 旧数据时尝试迁移到新位置，迁移成功后删除旧目录。

2.2.1 修复长截图在暂停恢复时偶尔接受异常大位移、插入重复内容的问题；回到初始视口后，改用更严格且连续一致的图像证据再开始反向扩展，避免周期性代码行被错误拼入、压缩或缺失。诊断日志同时补充重叠行数、置信度、横向偏移和预期位移，便于后续精准定位边缘场景。

2.2.0 允许直接点击已经放置的矩形、箭头、文字和表情进行二次编辑，可继续调整位置、大小、端点和边角，并为可选择、放置和编辑状态提供对应鼠标指针；矩形只在精准边界范围内响应选择，不会阻挡在内部继续放置其他标注。设置窗口每次重新显示到前台都会自动检测更新，并新增柔和的浅色渐变与深青亮色渐变配色。截图工具栏改用大小统一的镂空矢量图标（保留原有表情图标）。长截图进一步修复初始视口接缝、暂停收尾、返回后反向扩展以及周期性代码行中的整行缺失和压缩。

2.1.0 重构长截图为固定节拍的受控滚轮流程：支持单击开始、暂停和继续，双击返回初始位置后反向扩展，鼠标可自由移动；拼接锚点绑定截帧瞬间的输入位置，修复暂停、回程及周期性代码行场景中的缺行、重影、错误边界和永久校准。长图查看器同时支持适应窗口、`Ctrl + 鼠标滚轮` 缩放和无手柄拖动。此版本还加入跨平台拼接核心及 macOS 命令行原型，Windows 功能与数据目录保持兼容。

2.0.0 将软件更名为 SnapCut（简截）并启用全新图标；重写长截图拼接内核（链式顺序匹配、采样背压、DwmFlush 对齐拷屏、边界重锚），任意速度与来回滚动都能稳定拼接；实时预览改为全局整图并随图增高；后台静态内存降至约 25MB；表情贴纸升级为 88 个系统原版彩色表情并支持长截图编辑；箭头改为微信式锥形实心样式；新增应用级异常兜底，异常记录到 `ScreenshotData\Diagnostics\crash.log` 而不再闪退。**免安装版从 1.3.2 及更早版本升级时**：由于程序文件名由 Screenshot.exe 更名为 SnapCut.exe，旧版内置更新器无法自动完成本次替换，请手动下载新版免安装包解压使用（可把旧目录的 `ScreenshotData` 拷贝过去保留数据）；安装版用户仍可在程序内一键自动更新。

1.3.0 是首个支持在线覆盖更新的版本；1.3.1 起同时提供 Gitee 国内源和 GitHub 国际源。左侧“版本更新”页面会并发检测两个来源，采用先返回的有效清单，下载失败时自动换源，并严格校验文件大小与 SHA-256。安装版静默覆盖原目录，免安装版安全替换程序文件，两种方式都保留 `ScreenshotData` 并自动重启。由 1.2.0 或更早版本升级时需手动安装一次 1.3.1，之后即可在程序内更新。

安装程序不要求预先安装 .NET，并提供：

- 仅为当前用户安装，或使用管理员权限为所有用户安装
- 自定义安装位置
- 可选桌面快捷方式
- 开始菜单快捷方式和标准卸载入口
- 跟随 Windows 深浅色模式的安装界面

卸载时程序文件、快捷方式、卸载注册信息和当前用户的开机启动项会正常清理，并单独询问是否删除安装目录内的 `ScreenshotData`。默认选择保留以防误删截图；确认删除后会一并清理设置、历史、诊断文件、默认目录中的截图，以及当前用户可能存在的旧版数据。用户主动选择到其他目录保存的文件不会被卸载器删除。

### 默认快捷键

| 功能 | 快捷键 |
| --- | --- |
| 区域截图 | `Ctrl+Alt+S` |
| OCR | `Ctrl+Alt+O` |
| 钉图 | `Ctrl+Alt+P` |
| 打开设置 | `Ctrl+Alt+,` |

所有快捷键都可以在设置中修改或清空。

### 从源码构建

要求：Windows 10/11、.NET 8 SDK。

```powershell
dotnet restore
dotnet build Screenshot.sln -c Release
dotnet test Screenshot.sln -c Release
dotnet run --project src/Screenshot.App
```

生成自包含安装包和免安装包还需要 Inno Setup 6：

```powershell
winget install --id JRSoftware.InnoSetup --exact
.\installer\build-installer.ps1
```

安装包、免安装包和在线更新清单 `Screenshot-Update.json` 都输出到 `installer/dist/`，清单还会同步到 `updates/` 供 Gitee 固定地址读取。发布时三个文件需要同时上传到 GitHub 与 Gitee 的同版本 Release。项目架构见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)，实现进度见 [PLAN.md](PLAN.md)。

---

## English

SnapCut is a lightweight capture utility for Windows 10 and 11. It combines region capture, scrolling capture, annotation, OCR, pinned images, and optional translation in one compact workflow while keeping processing local whenever possible.

### Why SnapCut

- **Capture menus, drop-downs, and tooltips**: the hotkey freezes the visible desktop before focus-sensitive UI can close, preserving transient surfaces such as context menus, notification-area menus, drop-down lists, and hover tooltips for selection and editing.
- **Adjust and edit before finishing**: hover over another application to snap to its full window and click to select it, or drag to make a free-form selection. The desktop stays fixed while the selection moves, with a bright interior and dimmed exterior. After annotating, all eight handles remain available while protected bounds prevent existing ink from being cropped. Existing rectangles, arrows, text, and emoji can be clicked directly and then moved or resized, including arrow endpoints and rectangle corners, without reselecting a tool. Freehand drawing, mosaic, colors, stroke widths, undo, and redo remain available.
- **Controlled scrolling capture**: make a normal region selection, click the `↕` toolbar icon, then click inside the region to scroll downward slowly; double-click before starting to capture upward first. SnapCut sends fine-grained wheel messages at a fixed cadence without locking the pointer. Click to pause or resume; double-click during the first leg to settle, return quickly to the initial viewport without rewriting pixels, then continue in the opposite direction. At a real boundary scrolling stops while the current result remains available for editing, confirmation, or cancellation. The live preview always shows the complete result.
- **Local OCR with selectable image text**: uses the Windows OCR engine without uploading images, then overlays selectable text directly on captures, OCR result windows, and pinned images, copying it through the native Unicode clipboard. Translated overlays are selectable and copyable as well. Available languages depend on installed Windows language packs.
- **Translation under your control**: the capture toolbar automatically detects the source language, batch-translates OCR lines, and places translations over their original image locations. Copied and saved images include the translations, and the whole overlay can be undone in one step. Different OpenAI-compatible vendors are supported: after entering an endpoint and API key, users can explicitly fetch that vendor's model list or type a model id manually. An unchanged provider response is reported instead of being presented as a translation. Text is sent only after translation is explicitly enabled and requested, and API keys are protected with per-user Windows DPAPI encryption.
- **Fast daily workflow**: configurable global hotkeys, system tray controls, capture history, pinned images, startup behavior, light/dark themes, and a choice between minimizing or fully exiting when the window is closed. Whenever Settings returns to the foreground from the compact window, notification area, or hotkey, it checks for updates and shows a badge on the Update entry when a newer release exists. Gitee and GitHub are probed concurrently, with automatic fallback if a download fails. Installed and portable updates both preserve `ScreenshotData`. New installations show both taskbar and notification-area icons by default so the application remains easy to find.
- **Data stays with the application**: installed and portable builds keep settings, encrypted credentials, history, and default captures in `ScreenshotData` beside `SnapCut.exe`. Personal configuration is excluded from the repository.

### Download and install

Download the latest self-contained x64 build (release file names keep the legacy Screenshot- prefix so older builds can still self-update; the packaged program is SnapCut.exe):

- [SnapCut Setup 2.2.1 (GitHub)](https://github.com/jiuwanzi-hui/Screenshot/releases/latest/download/Screenshot-Setup-2.2.1-win-x64.exe)
- [SnapCut Portable 2.2.1 (GitHub)](https://github.com/jiuwanzi-hui/Screenshot/releases/latest/download/Screenshot-Portable-2.2.1-win-x64.zip)
- [SnapCut Setup 2.2.1 (Gitee China mirror)](https://gitee.com/wwangyunhui/screenshot/releases/download/v2.2.1/Screenshot-Setup-2.2.1-win-x64.exe)
- [SnapCut Portable 2.2.1 (Gitee China mirror)](https://gitee.com/wwangyunhui/screenshot/releases/download/v2.2.1/Screenshot-Portable-2.2.1-win-x64.zip)

Extract the portable archive and run `SnapCut.exe`; neither installation nor a preinstalled .NET runtime is required. Both packages store their data in `ScreenshotData` under their respective application directories, so back up that directory before moving or uninstalling the application. Version 2.2.1 retains the existing data layout and attempts to migrate legacy 1.0.0 `%LocalAppData%\Screenshot` data when found, removing the old directory after a successful migration.

Version 2.2.1 prevents scrolling capture from accepting implausibly large motion after pausing and resuming, which could insert repeated content. Reverse extension after returning to the initial viewport now requires stricter, consecutively consistent image evidence, preventing periodic code lines from being inserted at the wrong position, compressed, or omitted. Diagnostics also include overlap rows, confidence, horizontal offset, and expected motion for more precise investigation of edge cases.

Version 2.2.0 makes existing rectangles, arrows, text, and emoji directly selectable for later adjustment of position, size, endpoints, and corners, with distinct pointer feedback for selectable, placing, and editing states. Rectangles respond only on their precise border so their interior does not block placing another annotation. Settings now checks for updates every time it returns to the foreground and uses refreshed soft light gradients and deep cyan gradients in dark mode. Capture-toolbar actions use consistently sized outline vector icons while retaining the existing emoji icon. Scrolling capture receives additional fixes for the initial-viewport seam, pause settling, reverse extension after returning, and complete code lines lost or compressed on periodic content.

Version 2.1.0 rebuilds scrolling capture around fixed-cadence controlled wheel input: click to start, pause, or resume; double-click to return to the initial viewport and extend in the opposite direction; and move the pointer freely throughout. Stitch anchors now use the input position at the exact capture instant, fixing missing rows, ghost seams, false boundaries, and permanent alignment stalls after pauses or return trips on repetitive code. The tall-image viewer also gains fit-to-window sizing, `Ctrl + mouse wheel` zoom, and direct panning without a visible resize handle. This release includes the new cross-platform stitching core and a macOS command-line prototype while preserving Windows behavior and data layout.

Version 2.0.0 renames the application to SnapCut with a new icon, rewrites the scrolling-capture stitching core (sequential chain matching, sampling backpressure, DwmFlush-aligned capture, and boundary re-anchoring) so any speed and direction changes stitch reliably, shows the whole stitched image in a growing live preview, reduces idle memory to about 25 MB, upgrades stickers to 88 native color emoji (including the scrolling-capture editor), introduces WeChat-style tapered arrows, and adds an application-level exception net that logs to `ScreenshotData\Diagnostics\crash.log` instead of crashing. **Portable users upgrading from 1.3.2 or earlier**: because the executable was renamed from Screenshot.exe to SnapCut.exe, the old in-app updater cannot apply this release automatically - download the new portable package once manually (copy the old `ScreenshotData` folder over to keep your data). Installed builds still update in place automatically.

Version 1.3.0 introduced in-app updates. Starting with 1.3.1, the dedicated Update page probes the Gitee China mirror and GitHub concurrently, uses the first valid manifest, and switches source automatically if a download fails. Every package is checked against its declared size and SHA-256. Installed builds update in place; portable builds safely replace program files; both preserve `ScreenshotData` and restart automatically. Users on 1.2.0 or earlier need one final manual upgrade to 1.3.1, after which future releases can be installed in the app.

The installer does not require a preinstalled .NET runtime and includes:

- Current-user or administrator-backed all-users installation
- A user-selectable installation directory
- An optional desktop shortcut
- Start menu and standard uninstall entries
- An installer UI that follows the Windows light/dark appearance

Uninstall removes the program files, shortcuts, uninstall registration, and the current user's startup entry, then separately asks whether `ScreenshotData` in the installation directory should also be deleted. The default is to keep it to prevent accidental capture loss. Confirming deletion removes settings, history, diagnostics, captures in the default directory, and any legacy data for the current user. Files saved to a user-selected external directory are never deleted by the uninstaller.

### Default hotkeys

| Action | Hotkey |
| --- | --- |
| Region capture | `Ctrl+Alt+S` |
| OCR | `Ctrl+Alt+O` |
| Pin image | `Ctrl+Alt+P` |
| Open settings | `Ctrl+Alt+,` |

Every hotkey can be changed or cleared in Settings.

### Build from source

Requirements: Windows 10/11 and the .NET 8 SDK.

```powershell
dotnet restore
dotnet build Screenshot.sln -c Release
dotnet test Screenshot.sln -c Release
dotnet run --project src/Screenshot.App
```

Inno Setup 6 is also required to produce the installer and portable package:

```powershell
winget install --id JRSoftware.InnoSetup --exact
.\installer\build-installer.ps1
```

The installer, portable package, and `Screenshot-Update.json` online-update manifest are written to `installer/dist/`; the manifest is also copied to `updates/` for Gitee's stable raw URL. Upload all three files to matching GitHub and Gitee releases. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the architecture and [PLAN.md](PLAN.md) for implementation status.
