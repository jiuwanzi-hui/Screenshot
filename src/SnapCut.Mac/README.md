# SnapCut for macOS

`SnapCut.Mac` 是独立于 Windows WPF 前端的 macOS 菜单栏应用。它复用
`SnapCut.Core` 的平台无关拼接内核及现有 CoreGraphics 捕获层，Windows 工程
`Screenshot.App` 不引用本项目或 Avalonia。

## 当前功能

- 菜单栏常驻，单击图标打开设置与截图历史。
- `⌘⇧A` 区域截图、`⌘⇧S` 长截图，两组快捷键可在设置中修改。
- 框选前冻结当前桌面底图，框内保持原图、框外使用暗色遮罩。
- 普通截图自动保存到 `~/Pictures/SnapCut`，支持预览、复制、另存为和
  `Ctrl + 滚轮` 缩放。
- 长截图复用 `ScrollCaptureEngine`：框选后手动滚动，使用独立控制窗停止生成
  或取消；控制窗、设置窗和预览窗均设置为不参与屏幕捕获。
- 设置页显示屏幕录制/输入监控权限、快捷键、截图后行为和最近截图。
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

脚本输出 `artifacts/mac/SnapCut-<version>-<runtime>.app` 和 ZIP。在真实 Mac
上运行时会用 `chmod` 保留应用宿主的可执行权限，并使用 `ditto` 生成适合分发的
ZIP。传入 `-SignIdentity "Developer ID Application: ..."` 可执行 hardened
runtime 签名；正式对外发布前仍需 Apple 公证。

Windows 可以交叉发布两个运行时用于编译检查，但 Windows 生成的 ZIP 不保留
Unix 可执行位，不能作为正式 macOS 分发包。

## CLI 诊断

```bash
./snapcut displays
./snapcut permissions
./snapcut capture --rect 100,100,800,600 --out shot.png
./snapcut scroll --rect 100,100,800,600 --out long.png
```

## 验收边界

自动测试覆盖坐标缩放、快捷键匹配、设置持久化和历史排序；macOS 原生菜单栏、
权限弹窗、多显示器排列、Retina 混合缩放、剪贴板和实际滚动拼接必须在真实 Mac
上人工验收。ScreenCaptureKit 迁移、截图标注、OCR/翻译和钉图仍属于后续阶段。
