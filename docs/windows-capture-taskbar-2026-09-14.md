# 截图覆盖层与任务栏层级（2026-09-14）

用户反馈截图开始后任务栏被压到后台，而且后面的普通应用也会遮住任务栏。

代码中没有找到隐藏任务栏、改变系统工作区或直接降低任务栏层级的调用。发现整屏无边框截图窗口和原生遮罩未声明为非全屏窗口；普通截图的延迟首帧路径会先显示透明整屏窗口，再捕获桌面，因此可能在取样前触发 Shell 的全屏启发式判断。

Microsoft 文档明确说明，全屏识别可能把任务栏移到 Z-order 底部；`MarkFullscreenWindow(FALSE)` 仍允许自动识别，不能排除此行为。推荐在显示之前设置 `SetProp(hwnd, L"NonRudeHWND", 1)`。

- 官方接口说明：https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-itaskbarlist2-markfullscreenwindow
- 窗口显示时机说明：https://devblogs.microsoft.com/oldnewthing/20250522-00/?p=111211

修改只对当前进程自有覆盖层 HWND 设置该标记：WPF 截图窗口在 SourceInitialized 时设置；原生遮罩、框选辅助及尺寸提示窗口在 OnHandleCreated 时设置，包括句柄重建。录屏标注和输入展示覆盖层同步处理。没有改任务栏设置、其他应用的窗口属性、截图像素范围或长截图拼接算法；没有增加轮询或常驻资源。

Release 定向测试 12 项通过，0 跳过。仅为该轮显式启用 `SNAPCUT_RUN_DESKTOP_TESTS=1`，过滤为 OverlayShellBehaviorTests、CaptureStartupHandoffTests、CaptureInputReadinessTests。新增原生验证只创建隐藏窗口，不显示全屏测试图案、不注入全局输入、不修改任务栏：核对显示前的原生属性、辅助句柄重建、普通与延迟截图启动、录屏覆盖层和无关窗口不受影响。既有启动与取消回归通过。

这验证了触发路径的防护，不等同于在客户机器复现并证明所有系统层级异常都消失。客户仍需检查截图时能否框选任务栏，以及完成、取消后普通应用是否继续遮挡任务栏。
