# Screenshot

[简体中文](#简体中文) | [English](#english)

![Screenshot icon](src/Screenshot.App/Assets/Screenshot.png)

## 简体中文

Screenshot 是一款面向 Windows 10/11 的轻量截图工具。它把区域截图、长截图、标注、OCR、钉图和可选翻译放在同一条紧凑工作流中，并优先在本机完成处理。

### 主要优点

- **截图后直接调整和编辑**：框选后可以继续移动、缩放选区，并使用矩形、箭头、画笔、文字、马赛克、颜色、线宽、撤销和重做。
- **双向长截图**：支持向上或向下滚动采集、重叠区域匹配、实时预览和结果编辑，适合网页、聊天记录与文档。
- **本地 OCR**：使用 Windows 本地 OCR 引擎，图片不需要上传；识别语言取决于系统已安装的语言包。
- **可控的在线翻译**：只有用户明确启用并点击翻译时才发送文字；支持 OpenAI 兼容接口，API Key 使用当前用户 DPAPI 加密保存。
- **高效的日常操作**：支持全局快捷键、系统托盘、截图历史、钉图、开机启动、深浅色主题，以及关闭窗口时最小化到后台或彻底退出。
- **数据跟随程序目录**：安装版与免安装版都把设置、加密凭据、历史和默认截图保存在 `Screenshot.exe` 旁的 `ScreenshotData`，不会把个人配置提交到源码仓库。

### 下载与安装

下载最新的 x64 自包含版本：

- [安装版 Screenshot Setup 1.0.2](https://github.com/jiuwanzi-hui/Screenshot/releases/latest/download/Screenshot-Setup-1.0.2-win-x64.exe)
- [免安装版 Screenshot Portable 1.0.2](https://github.com/jiuwanzi-hui/Screenshot/releases/latest/download/Screenshot-Portable-1.0.2-win-x64.zip)

免安装版解压后直接运行 `Screenshot.exe`，不需要安装或预先配置 .NET。安装版和免安装版的数据都保存在各自程序目录的 `ScreenshotData` 中；移动或卸载程序前请按需备份该目录。1.0.2 安装版首次启动时会尝试把 1.0.0 的 `%LocalAppData%\Screenshot` 数据迁移到新位置，迁移成功后删除旧目录。

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
| 长截图 | `Ctrl+Alt+L` |
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

两个发布包都输出到 `installer/dist/`。项目架构见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)，实现进度见 [PLAN.md](PLAN.md)。

---

## English

Screenshot is a lightweight capture utility for Windows 10 and 11. It combines region capture, scrolling capture, annotation, OCR, pinned images, and optional translation in one compact workflow while keeping processing local whenever possible.

### Why Screenshot

- **Adjust and edit before finishing**: move or resize the selection after capture, then use rectangles, arrows, freehand drawing, text, mosaic, colors, stroke widths, undo, and redo.
- **Bidirectional scrolling capture**: capture while scrolling up or down, match overlapping content, preview progress live, and edit the composed result.
- **Local OCR**: uses the Windows OCR engine without uploading images. Available languages depend on the Windows language packs installed on the machine.
- **Translation under your control**: text is sent only after translation is explicitly enabled and requested. OpenAI-compatible endpoints are supported, and API keys are protected with per-user Windows DPAPI encryption.
- **Fast daily workflow**: configurable global hotkeys, system tray controls, capture history, pinned images, startup behavior, light/dark themes, and a choice between minimizing or fully exiting when the window is closed.
- **Data stays with the application**: installed and portable builds keep settings, encrypted credentials, history, and default captures in `ScreenshotData` beside `Screenshot.exe`. Personal configuration is excluded from the repository.

### Download and install

Download the latest self-contained x64 build:

- [Screenshot Setup 1.0.2](https://github.com/jiuwanzi-hui/Screenshot/releases/latest/download/Screenshot-Setup-1.0.2-win-x64.exe)
- [Screenshot Portable 1.0.2](https://github.com/jiuwanzi-hui/Screenshot/releases/latest/download/Screenshot-Portable-1.0.2-win-x64.zip)

Extract the portable archive and run `Screenshot.exe`; neither installation nor a preinstalled .NET runtime is required. Both packages store their data in `ScreenshotData` under their respective application directories, so back up that directory before moving or uninstalling the application. On first launch, the 1.0.2 installer build attempts to migrate legacy 1.0.0 `%LocalAppData%\Screenshot` data and removes the old directory after a successful migration.

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
| Scrolling capture | `Ctrl+Alt+L` |
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

Both release packages are written to `installer/dist/`. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the architecture and [PLAN.md](PLAN.md) for implementation status.
