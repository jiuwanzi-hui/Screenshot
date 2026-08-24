# SnapCut for macOS

`SnapCut.Mac` 是独立于 Windows WPF 前端的 macOS 菜单栏应用。它复用
`SnapCut.Core` 的平台无关拼接内核及现有 CoreGraphics 捕获层，Windows 工程
`Screenshot.App` 不引用本项目或 Avalonia。

## 当前功能

- 菜单栏常驻，单击图标打开设置与截图历史。
- `⌘⇧A` 区域截图、`⌘⇧S` 长截图，两组快捷键可在设置中修改。
- 框选前冻结当前桌面底图，框内保持原图、框外使用暗色遮罩。
- 框选时按显示器实际缩放动态显示像素分辨率，支持 Esc 或右键取消。
- 框选前实时显示鼠标位置的十六进制颜色值，按 `C` 复制。
- 普通截图在选区旁直接提供矩形、椭圆、箭头、画笔、文字、表情、自动序号、
  马赛克、颜色、线宽及撤销/重做；标注合成进最终 PNG，颜色和线宽跨重启保存。
- 普通截图自动保存到 `~/Pictures/SnapCut`，支持预览、复制、另存为和
  `⌘/Ctrl + 滚轮` 缩放；预览支持 `⌘C`、`⌘S`、`⌘O` 和 Esc。
- 预览可直接创建置顶钉图；钉图支持拖动、滚轮缩放、透明度、复制、另存为、
  双击恢复原始大小和右键关闭。
- 钉图支持持久化位置、缩放、透明度和隐藏状态、窗口裁剪、旁置编辑工具栏、
  组合/解除组合与组合图片复制。
- 录屏支持 MP4（H.264/H.265）、GIF、WebP、帧率、麦克风/系统声音选项、
  鼠标点击和键盘输入提示；结束后可按开始/结束帧预览、播放预剪辑片段并导出。
- 内容识别支持 macOS Vision OCR（Intel 原生）、Apple Silicon PP-OCRv6 辅助进程、
  二维码/条码、隐私候选确认打码、在线 OpenAI 兼容翻译和 Bergamot 离线翻译。
- 工具栏支持功能显隐、一行/两行布局、实时预览；全局快捷键覆盖截图、长截图、
  录屏、OCR、翻译、钉图和设置。颜色、透明度、线宽、常用颜色、主题和启动项均持久化。
- 长截图复用 `ScrollCaptureEngine`：框选后手动滚动，使用独立控制窗停止生成
  或取消；控制窗、设置窗和预览窗均设置为不参与屏幕捕获。
- 设置页显示屏幕录制/输入监控权限、快捷键、截图后行为和最近截图。
- 截图历史数量可配置为 1–100，保存新截图时自动清理超出上限的旧 PNG。
- 保留 `displays`、`permissions`、`capture`、`scroll` CLI 命令用于诊断。

## 权限

| 功能 | macOS 权限 | 缺失时行为 |
| --- | --- | --- |
| 区域截图、长截图 | 屏幕录制 | 首次调用申请；拒绝后显示具体授权路径 |
| 全局快捷键、滚轮方向提示 | 输入监控 | 菜单栏仍可用；设置页可重新申请 |

授权位置：系统设置 → 隐私与安全性 → 屏幕录制 / 输入监控。macOS 可能要求
退出并重新打开 SnapCut 才能应用新权限。

## 构建 `.app`

要求 .NET 8 SDK。Apple Silicon：

```powershell
pwsh ./build-macos.ps1 -Runtime osx-arm64 -Version 0.1.0
```

Intel：

```powershell
pwsh ./build-macos.ps1 -Runtime osx-x64 -Version 0.1.0
```

脚本输出固定内部名称的 `artifacts/mac/SnapCut.app`，以及固定下载名称的
`SnapCut-osx-arm64.zip/.tar.gz` 或 `SnapCut-osx-x64.zip/.tar.gz`。
固定 `.app` 名称可避免每次测试构建都以新应用名称重新出现在权限列表。在真实 Mac
上运行时会用 `chmod` 保留应用宿主的可执行权限，并使用 `ditto` 生成适合分发的
ZIP。传入 `-SignIdentity "Developer ID Application: ..."` 可执行 hardened
runtime 签名；正式对外发布前仍需 Apple 公证。

Windows 可以交叉发布两个运行时用于编译检查。Windows 生成的 ZIP 不保留
Unix 可执行位；构建脚本会同时生成保留主程序 `0755` 权限的 `.tar.gz`，可用于
虚拟机测试。正式对外分发仍应在 macOS 上生成 ZIP，并完成签名与公证。

## CLI 诊断

```bash
./snapcut displays
./snapcut permissions
./snapcut capture --rect 100,100,800,600 --out shot.png
./snapcut scroll --rect 100,100,800,600 --out long.png
```

## 验收边界

自动测试覆盖坐标缩放、快捷键匹配、设置持久化、历史排序、标注像素合成和隐私
候选处理；macOS 原生菜单栏、权限弹窗、多显示器排列、Retina 混合缩放、Vision
OCR、FFmpeg 录屏、剪贴板和实际滚动拼接仍必须在真实 Mac 上人工验收。
