# SnapCut · 简截

[简体中文](#简体中文) · [English](#english)

[![Release](https://img.shields.io/github/v/release/jiuwanzi-hui/Screenshot?style=flat-square)](https://github.com/jiuwanzi-hui/Screenshot/releases/latest)
[![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?style=flat-square)](https://github.com/jiuwanzi-hui/Screenshot/releases/latest)
[![macOS](https://img.shields.io/badge/macOS-Intel%20%7C%20Apple%20Silicon-7657FF?style=flat-square)](https://github.com/jiuwanzi-hui/Screenshot/releases/latest)
[![License](https://img.shields.io/badge/license-personal%20non--commercial-D94B64?style=flat-square)](LICENSE)
[![Gitee](https://img.shields.io/badge/Gitee-国内下载-C71D23?style=flat-square)](https://gitee.com/wwangyunhui/screenshot/releases)

<img src="src/Screenshot.App/Assets/Screenshot.png" width="80" alt="SnapCut 图标">

> 从截取画面到标注、识别、翻译和分享，在同一条工作流中完成。

## 简体中文

SnapCut（简截）是一款面向 Windows 和 macOS 的桌面截图工具。截图、长截图、图片编辑、钉图、OCR、翻译、AI 问图、剪贴板历史与区域录屏集中在一起，减少工具之间的来回切换。

### 特点与功能

- **截取与编辑连在一起**：区域框选、窗口吸附、全屏截图，支持滚轮和自动滚动长截图；预览后直接标注、复制或保存，也能打开已有图片编辑。
- **标注更灵活**：形状、箭头、序号、文字、荧光笔、马赛克、花括号、引用批注与水印，支持撤销和重做。内置彩色表情，也可导入自己的 PNG、JPEG、GIF、WebP 贴图；动态画面可导出 GIF，默认原始尺寸，可另选最长边 960 像素的聊天尺寸。
- **把参考图留在桌面**：截图或图片可钉在桌面上，支持缩放、旋转、镜像、透明度调整及继续标注，边参考边工作。
- **图片里的内容可以继续用**：本地 OCR 提取文字，支持表格、公式识别及 Markdown 转换；识别结果可复制、查看原文或手动继续翻译。
- **按需选择翻译与 AI**：截图翻译、文本翻译、实时区域翻译可对照原文、译文与译图；在线服务与离线模型按用户设置的顺序使用。AI 问图可围绕截图或导入图片提问，使用自己配置的模型服务。
- **剪贴板更好整理**：搜索文字、图片和文件等历史，支持常用、置顶、标签、预览与分类清理；可配对多台设备，通过加密局域网连接同步所选内容。
- **录下操作过程**：录制指定区域，支持暂停、继续及鼠标／按键提示；系统声音、麦克风等选项按平台与设备提供。
- **按自己的习惯使用**：可调整快捷键、工具栏顺序、字体、深浅色与玻璃主题，导入导出自建主题，并分类备份配置、模型、历史和表情包。

### 下载与平台

| 平台 | 支持范围 | 下载 |
| --- | --- | --- |
| Windows | Windows 10 / 11，x64；不支持 Windows 7 | [GitHub 发行页](https://github.com/jiuwanzi-hui/Screenshot/releases/latest) · [Gitee 国内发行页](https://gitee.com/wwangyunhui/screenshot/releases) |
| macOS | macOS 13.3 及以上，Intel / Apple Silicon | [GitHub 发行页](https://github.com/jiuwanzi-hui/Screenshot/releases/latest)，选择对应架构 DMG |

Windows 提供安装版 EXE 和免安装版 ZIP／7z，无需预装 .NET；便携包完整解压后运行 `SnapCut.exe`。Gitee 提供 Windows EXE 与 7z。Windows 可在程序内检查和安装更新，更新内容查看发行页。

macOS 使用菜单栏入口，提供完整 DMG。目前采用固定自签名证书，未经过 Apple 公证；安装到 `/Applications/SnapCut.app`，按提示检查证书信任与相关系统权限。应用可检测并下载更新，由用户手动安装。Gitee 不提供 Mac 包。

两端保留原生操作习惯。Windows OCR 仅使用 PP-OCRv6 Small／Medium，macOS 另保留系统 Vision 与自动选择。macOS 普通截图完成后直接复制、按设置保存，编辑由用户主动打开。

### 开始使用

1. 启动后，从托盘／菜单栏或快捷键发起截图。
2. 框选后通过工具栏标注、识别、翻译、录屏或钉图；菜单中的“编辑图片”可处理已有文件。
3. 在设置中选择保存位置、快捷键和模型服务。离线翻译模型与运行库不随主安装包附带，需要时主动下载或导入，准备完成后可离线使用。

| 常用操作 | Windows 默认 | macOS 默认 |
| --- | --- | --- |
| 区域截图 | `Ctrl+Alt+S` | `⌘⇧A` |
| 文字识别 | `Ctrl+Alt+O` | `⌘⇧O` |
| 钉图 | `Ctrl+Alt+P` | `⌘⇧P` |
| 打开设置 | `Ctrl+Alt+,` | `⌘⇧,` |

快捷键可修改；文字识别、截图翻译与文本翻译是独立操作。Windows 还支持鼠标侧键与带修饰键的鼠标长按配置。

### 数据与隐私

本地 OCR 和已安装的离线翻译模型在本机处理内容；使用在线翻译或 AI 时，对应文字或图片会发送给所配置的服务提供方。局域网剪贴板同步默认关闭，只连接主动配对的设备，可分别选择内容类型。

Windows 的设置、历史与默认截图位于程序旁的 `ScreenshotData`，模型另有独立目录；macOS 使用本机用户数据目录。保存位置、模型路径和保留策略可在设置中查看；移动或卸载前可用分类备份保留需要的数据。

<a id="community"></a>

### 反馈与交流

- 可复现的问题：[GitHub Issues](https://github.com/jiuwanzi-hui/Screenshot/issues)。请附系统版本、操作步骤及必要截图，公开前遮住敏感内容。
- 体验与建议：[GitHub Discussions](https://github.com/jiuwanzi-hui/Screenshot/discussions)。
- 扫码加入 SnapCut 交流群，或在软件“交流联系”中查看群二维码。

<img src="src/Screenshot.App/Assets/SnapCutCommunityQr.jpg" width="300" alt="SnapCut 交流群二维码">

### 许可

SnapCut 供个人非商业使用，**不属于 OSI 定义的开源软件**。商业、盈利、企业内部业务用途及二进制再分发须事先取得作者书面许可，详见 [LICENSE](LICENSE)。后续产品源码不公开；历史源码及第三方组件分别遵循适用许可证。

---

## English

SnapCut brings screen capture, annotation, OCR, translation and sharing into one desktop workflow for Windows and macOS. It also includes pinned images, image editing, AI image questions, clipboard history and region recording.

### Features

- **Capture and edit:** region selection, window snapping, full-screen and manual/automatic scrolling capture. Preview, annotate, copy or save the result; open existing images for editing too.
- **Flexible annotation:** shapes, arrows, numbering, text, highlighters, mosaic, braces, callouts and watermarks, with undo/redo. Use built-in emoji or import PNG, JPEG, GIF and WebP stickers. Export animations as GIF at original size, or explicitly choose a proportional chat size with a 960-pixel longest edge.
- **Desktop references:** pin images with zoom, rotation, mirroring, opacity and annotation controls.
- **Reusable image content:** extract text locally, recognize tables and formulas, or convert content to Markdown. Inspect, copy or translate the result.
- **Translation and AI:** screenshot, text and live-region translation, with source, translation and image views. Online/offline providers follow your configured order. Ask questions about captures or imported images using your chosen model service.
- **Clipboard management:** search text, image and file history; organize favorites, pins and tags; preview or clean selected categories. Pair multiple devices for encrypted LAN sync of selected content.
- **Region recording:** pause and resume, with optional pointer/key indicators. Audio options depend on the platform and devices.
- **Personalization:** configure shortcuts, toolbar order, fonts and light/dark/glass themes. Import/export custom themes and back up selected settings, models, history and stickers.

### Download and getting started

| Platform | Requirements | Downloads |
| --- | --- | --- |
| Windows | Windows 10 / 11, x64; Windows 7 is unsupported | [GitHub Releases](https://github.com/jiuwanzi-hui/Screenshot/releases/latest) · [Gitee Releases](https://gitee.com/wwangyunhui/screenshot/releases) |
| macOS | macOS 13.3 or later, Intel / Apple Silicon | [GitHub Releases](https://github.com/jiuwanzi-hui/Screenshot/releases/latest), with separate DMGs for each architecture |

Windows offers an EXE installer and portable ZIP/7z packages, without a separate .NET installation. Extract the complete portable package before running `SnapCut.exe`. Gitee hosts Windows EXE and 7z packages. Windows can check for and install updates within the app.

Mac DMGs currently use a fixed self-signed certificate and are not Apple-notarized. Install to `/Applications/SnapCut.app` and follow the certificate-trust and system-permission guidance. The app can download updates; installation is manual. Mac packages are hosted on GitHub only.

Windows uses a tray menu and PP-OCRv6 Small/Medium. macOS uses a menu-bar app and also offers Vision and automatic OCR selection. Ordinary Mac captures copy directly and save according to settings; editing opens when requested.

Start a capture from the app menu or a shortcut, then use its toolbar. Defaults are `Ctrl+Alt+S` on Windows and `⌘⇧A` on Mac for capture; `Ctrl+Alt+O` / `⌘⇧O` for OCR. All bindings are configurable, and OCR, screenshot translation and text translation are separate actions. Select “Edit image” to work with an existing file.

Offline translation models and runtimes are optional downloads/imports, separate from the main package. Once installed, they can run without a network connection.

### Data, feedback and license

Local OCR and installed offline models process content on your computer. Online translation and AI send relevant text or images to your configured provider. LAN clipboard sync is off by default and connects only to paired devices.

Windows keeps settings, history and default captures in `ScreenshotData` beside the program, with separate model directories. macOS uses its local user-data directory. Check Settings for destinations and retention, and use backups before moving or removing data.

Report bugs through [Issues](https://github.com/jiuwanzi-hui/Screenshot/issues), or share ideas in [Discussions](https://github.com/jiuwanzi-hui/Screenshot/discussions). Include your OS and reproduction steps, and redact sensitive content before posting. The [community QR code](#community) is also available in the app.

SnapCut is licensed for **personal, non-commercial use** and is not OSI open-source software. Commercial use, internal business use and binary redistribution require prior written permission; see [LICENSE](LICENSE). New product source is not published. Historical source and third-party components retain their applicable licenses.