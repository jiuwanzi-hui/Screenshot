# Screenshot

[简体中文](#简体中文) | [English](#english)

![Screenshot icon](src/Screenshot.App/Assets/Screenshot.png)

## 简体中文

Screenshot 是一款面向 Windows 10/11 的轻量截图工具。它把区域截图、长截图、标注、OCR、钉图和可选翻译放在同一条紧凑工作流中，并优先在本机完成处理。

### 主要优点

- **菜单、下拉框和悬浮提示也能截到**：快捷键触发时先冻结当前桌面画面，尽量保留右键菜单、通知区域菜单、下拉列表、悬浮提示等容易因失去焦点而消失的临时界面，再进入框选和编辑。
- **截图后直接调整和编辑**：鼠标悬停到其他软件时可自动吸附整个窗口，单击直接选取，也可继续拖动自由框选；移动选区时底图保持固定，框内明亮、框外变暗。编辑后仍可用八个控制点扩展或缩小选区，边缘不会越过已有标注；并提供矩形、箭头、画笔、文字、12 种彩色表情贴纸、马赛克、颜色、线宽、撤销和重做。
- **双向长截图**：普通截图框选后直接点击工具栏的 `↕` 图标，沿用当前选区开始向上或向下滚动采集；支持重叠区域匹配、实时预览和结果编辑。
- **本地 OCR 与图片文字选择**：使用 Windows 本地 OCR 引擎，图片不需要上传；识别后可直接在截图上拖选文字并通过原生 Unicode 剪贴板复制，译文覆盖完成后同样可以选择和复制，识别语言取决于系统已安装的语言包。
- **可控的在线翻译**：截图工具栏可自动检测原文语言、批量翻译 OCR 文字并按原行位置覆盖到图片，复制和保存会包含译文，整组译文可一步撤销；支持不同厂家的 OpenAI 兼容接口，配置服务地址和 API Key 后可主动获取该厂家的模型列表，也可手动输入模型标识。服务原样返回原文时会明确提示而不生成假译文。只有用户明确启用并点击翻译时才发送文字，API Key 使用当前用户 DPAPI 加密保存。
- **高效的日常操作**：支持全局快捷键、系统托盘、截图历史、钉图、开机启动、深浅色主题，以及关闭窗口时最小化到后台或彻底退出；设置页显示当前版本，可从 GitHub 检查、校验并覆盖更新，安装版和免安装版都会保留 `ScreenshotData`；首次运行默认同时显示任务栏图标和通知区域图标，避免用户找不到程序入口。
- **数据跟随程序目录**：安装版与免安装版都把设置、加密凭据、历史和默认截图保存在 `Screenshot.exe` 旁的 `ScreenshotData`，不会把个人配置提交到源码仓库。

### 下载与安装

下载最新的 x64 自包含版本：

- [安装版 Screenshot Setup 1.3.0](https://github.com/jiuwanzi-hui/Screenshot/releases/latest/download/Screenshot-Setup-1.3.0-win-x64.exe)
- [免安装版 Screenshot Portable 1.3.0](https://github.com/jiuwanzi-hui/Screenshot/releases/latest/download/Screenshot-Portable-1.3.0-win-x64.zip)

免安装版解压后直接运行 `Screenshot.exe`，不需要安装或预先配置 .NET。安装版和免安装版的数据都保存在各自程序目录的 `ScreenshotData` 中；移动或卸载程序前请按需备份该目录。1.3.0 延续 1.0.2 的数据布局，并会在发现 1.0.0 的 `%LocalAppData%\Screenshot` 旧数据时尝试迁移到新位置，迁移成功后删除旧目录。

1.3.0 是首个支持在线覆盖更新的版本：左侧“版本更新”页面显示当前版本，可检查 GitHub 最新正式版、下载并校验文件大小与 SHA-256。安装版静默覆盖原目录，免安装版安全替换程序文件，两种方式都保留 `ScreenshotData` 并自动重启。由 1.2.0 或更早版本升级时仍需手动安装一次 1.3.0，之后即可在程序内更新。

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

安装包、免安装包和在线更新清单 `Screenshot-Update.json` 都输出到 `installer/dist/`。发布时三个文件需要一起上传到同一个 GitHub Release。项目架构见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)，实现进度见 [PLAN.md](PLAN.md)。

---

## English

Screenshot is a lightweight capture utility for Windows 10 and 11. It combines region capture, scrolling capture, annotation, OCR, pinned images, and optional translation in one compact workflow while keeping processing local whenever possible.

### Why Screenshot

- **Capture menus, drop-downs, and tooltips**: the hotkey freezes the visible desktop before focus-sensitive UI can close, preserving transient surfaces such as context menus, notification-area menus, drop-down lists, and hover tooltips for selection and editing.
- **Adjust and edit before finishing**: hover over another application to snap to its full window and click to select it, or drag to make a free-form selection. The desktop stays fixed while the selection moves, with a bright interior and dimmed exterior. After annotating, all eight handles remain available while protected bounds prevent existing ink from being cropped. Editing includes rectangles, arrows, freehand drawing, text, 12 colored emoji stickers, mosaic, colors, stroke widths, undo, and redo.
- **Bidirectional scrolling capture**: make a normal region selection and click the `↕` toolbar icon to reuse that region for upward or downward scrolling capture, with overlap matching, live preview, and result editing.
- **Local OCR with selectable image text**: uses the Windows OCR engine without uploading images, then overlays selectable text directly on the capture and copies it through the native Unicode clipboard. Translated overlays are selectable and copyable as well. Available languages depend on installed Windows language packs.
- **Translation under your control**: the capture toolbar automatically detects the source language, batch-translates OCR lines, and places translations over their original image locations. Copied and saved images include the translations, and the whole overlay can be undone in one step. Different OpenAI-compatible vendors are supported: after entering an endpoint and API key, users can explicitly fetch that vendor's model list or type a model id manually. An unchanged provider response is reported instead of being presented as a translation. Text is sent only after translation is explicitly enabled and requested, and API keys are protected with per-user Windows DPAPI encryption.
- **Fast daily workflow**: configurable global hotkeys, system tray controls, capture history, pinned images, startup behavior, light/dark themes, and a choice between minimizing or fully exiting when the window is closed. Settings displays the current version and can check, verify, and apply GitHub updates in place; installed and portable updates both preserve `ScreenshotData`. New installations show both taskbar and notification-area icons by default so the application remains easy to find.
- **Data stays with the application**: installed and portable builds keep settings, encrypted credentials, history, and default captures in `ScreenshotData` beside `Screenshot.exe`. Personal configuration is excluded from the repository.

### Download and install

Download the latest self-contained x64 build:

- [Screenshot Setup 1.3.0](https://github.com/jiuwanzi-hui/Screenshot/releases/latest/download/Screenshot-Setup-1.3.0-win-x64.exe)
- [Screenshot Portable 1.3.0](https://github.com/jiuwanzi-hui/Screenshot/releases/latest/download/Screenshot-Portable-1.3.0-win-x64.zip)

Extract the portable archive and run `Screenshot.exe`; neither installation nor a preinstalled .NET runtime is required. Both packages store their data in `ScreenshotData` under their respective application directories, so back up that directory before moving or uninstalling the application. Version 1.3.0 keeps the 1.0.2 data layout and attempts to migrate legacy 1.0.0 `%LocalAppData%\Screenshot` data when found, removing the old directory after a successful migration.

Version 1.3.0 is the first release with in-app updates. Its dedicated Update page checks the latest stable GitHub release, downloads it, and verifies both size and SHA-256. Installed builds update in place; portable builds safely replace program files; both preserve `ScreenshotData` and restart automatically. Users on 1.2.0 or earlier need one final manual upgrade to 1.3.0, after which future releases can be installed in the app.

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

The installer, portable package, and `Screenshot-Update.json` online-update manifest are written to `installer/dist/`. Upload all three files to the same GitHub Release. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the architecture and [PLAN.md](PLAN.md) for implementation status.
