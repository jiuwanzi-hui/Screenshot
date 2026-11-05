# Windows 钉图高回报率拖动优化（2026-09-20）

用户反馈：鼠标设为 4 kHz 时，钉图窗口移动卡顿；降至 125 Hz 后正常。只修改 Windows，沿用 4.1.7。

## 代码路径与改动

原路径虽然使用 `CompositionTarget.Rendering` 合并位置更新，但每个 `WM_MOUSEMOVE` 仍进入 WPF 输入提供方，再在 `PreviewMouseMove` 阶段标记已处理。后者不能消除此前的输入处理开销；窗口位置更新也依赖透明 WPF 窗口的渲染回调。没有采集用户实体鼠标的跟踪数据，因此不把这两处代码风险当成已测定的全部根因。

`NativeWindowDragTracker` 的单图和编组共用路径现在：

- 仅在整窗拖动期间安装当前 HWND 的原生消息处理，提前处理移动消息，避免逐包进入 WPF 输入阶段。正常悬停、文字选择、编辑和裁剪不安装此处理。
- 使用现有原生帧时钟，按起始显示器的刷新率预算读取最新物理屏幕坐标，最多保留一个待处理的界面回调。现有时钟预算为 30–240 Hz，探测失败采用 60 Hz；无需按鼠标报文频率移动窗口。
- 仍在窗口所属界面线程移动 HWND，不跨线程同步移动，不重采样图片。
- 松手立即提交最终位置；丢失捕获、取消或关闭时停止，不追随之后的鼠标位置。放行按键释放消息供 WPF 同步输入状态。
- 每次拖动结束解除消息处理、停止并释放帧时钟；后台线程收到退出信号自行结束，界面线程不等待它。单图、编组关闭时都调用 `Dispose`。

消息处理顺序参考 [.NET 8 WPF HwndSource 源码](https://github.com/dotnet/wpf/blob/v8.0.0/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/InterOp/HwndSource.cs) 及 [HwndWrapper 源码](https://github.com/dotnet/wpf/blob/v8.0.0/src/Microsoft.DotNet.Wpf/src/Shared/MS/Win32/HwndWrapper.cs)，并通过真实隐藏 HWND 的消息验证。

## 验证结果

- Release 编译成功。编译器提示为已有 OCR／设置／测试代码的分析警告，本次拖动修改无新增警告。
- `NativeWindowDragTrackerTests` 新增 10 项全部通过。
  - 125、4,000、8,000 次报文密度，60／144／240 Hz 帧预算：拖动期下游收不到逐包移动消息，位置更新次数受帧预算约束，每帧使用最新坐标；松手立即落位、窗口尺寸不变、停止后正常输入恢复。
  - 约一秒内向隐藏窗口持续投递 4,000 条真实 `WM_MOUSEMOVE` 消息，原生帧时钟继续更新，没有等待输入队列清空后才移动。
  - 捕获失败、捕获转移、取消、缺失松手消息、快速重新拖动及停止后的旧回调均覆盖。
  - 连续三次拖动均验证原生时钟线程实际退出；等待退出仅发生在测试线程，产品界面线程不等待。
- 拖动、指针、钉图及自动 OCR 相关回归：44 项通过、21 项真实桌面集成测试按项目约定跳过。
- 缩放与原始像素清晰度回归：36 项通过。
- 合计 80 项通过、21 项跳过，`git diff --check` 无空白错误。

```powershell
dotnet test tests/Screenshot.App.Tests/Screenshot.App.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~NativeWindowDragTrackerTests|FullyQualifiedName~PointerPresentationTests|FullyQualifiedName~PinnedImageManagerTests|FullyQualifiedName~PinnedImageWindowTests|FullyQualifiedName~AutomaticOcr'
dotnet test tests/Screenshot.App.Tests/Screenshot.App.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~PinnedZoomInteractionTests|FullyQualifiedName~PinPixelFidelityTests'
```

验证未移动或捕获用户的实体鼠标。合成消息验证调度和输入路径，不等于真实 4 kHz HID 输入、可见窗口的 GPU 合成帧率或不同硬件上的手感；实体鼠标和跨不同刷新率显示器的连续拖动仍需实际试用确认。未测量可见拖动的峰值显存，不能据隐藏窗口测试宣称全硬件已解决。

本次测试没有生成额外的图片、录屏、临时脚本或模型副本。此前 OCR 任务遗留的测试目录不在本次清理范围。
