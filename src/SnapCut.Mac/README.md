# SnapCut.Mac — macOS 前端（CLI 原型阶段）

共享 `SnapCut.Core` 拼接内核的 macOS 前端。当前形态是命令行原型，用于在真实
macOS 抓屏 / 滚轮链路上验收核心算法；后续的菜单栏 App 将复用这里的
捕获层（`Capture/`）与原生绑定（`Native/`）。

## 构建与运行（在 Mac 上）

```bash
# 任一平台都可编译；运行需要 macOS
dotnet publish src/SnapCut.Mac -c Release -r osx-arm64 --self-contained
# Intel 芯片: -r osx-x64

./src/SnapCut.Mac/bin/Release/net8.0/osx-arm64/publish/snapcut displays
./src/SnapCut.Mac/bin/Release/net8.0/osx-arm64/publish/snapcut capture --out shot.png
./src/SnapCut.Mac/bin/Release/net8.0/osx-arm64/publish/snapcut scroll --rect 100,100,800,600 --out long.png
```

## 权限

| 功能 | 所需权限 | 缺失时行为 |
| --- | --- | --- |
| 抓屏（`capture`/`scroll`） | 屏幕录制 | 首次调用触发系统弹窗；拒绝则报错退出 |
| 滚轮监听（`scroll`） | 输入监控 | 打印警告后继续，滚动方向完全由图像证据决定 |

到 系统设置 → 隐私与安全性 中对运行 `snapcut` 的终端（或未来的 App 本体）授权。

## 技术要点

- 全部原生调用是 C ABI（CoreGraphics / CoreFoundation / ImageIO / CGEventTap），
  纯 P/Invoke，无 Objective-C 运行时依赖，因此 Windows 上也能交叉编译。
- 抓屏用 `CGDisplayCreateImageForRect`（macOS 15 起标记弃用但仍可用；迁移到
  ScreenCaptureKit 需要 ObjC 互操作，列为后续项）。HiDPI 显示器返回物理像素
  （2x 屏上 100pt 宽的区域产出 200px 帧），拼接核心直接工作在物理像素上。
- 像素统一归一化为 BGRA 后进入 `SnapCut.Core.PixelImage`；保存 PNG 走 ImageIO。
- 滚动采集引擎按 Windows 版验证过的规则实现：链式顺序拼接、采样背压
  （积压超容量 1/4 跳拍，位移 ≥ 1/4 视口或 120ms 未采样强制采样）、
  超容量丢中间帧并合并滚轮位移、无法定位的帧丢像素但位移并入后继帧。

## 已知差距（相对 Windows 版 / 待办）

- 无 UI：框选、遮罩、预览、编辑、托盘都未实现（等菜单栏 App 阶段）。
- ScreenCaptureKit 迁移、Retina 混合 DPI 多屏的坐标细节、自然滚动方向的
  精确判定（当前滚轮方向仅作偏好，判错只损失一次廉价反向探测）。
