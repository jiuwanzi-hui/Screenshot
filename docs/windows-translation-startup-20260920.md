# Windows 离线翻译启动失败排查（2026-09-20）

## 用户证据与结论边界

用户提供 GTX 1650 Ti（4 GB 独显）与 AMD 核显的电脑截图。任务管理器显示约 31.4 GB 内存、16.6 GB 已使用，独显专用显存约 0.4/4 GB；SnapCut 却提示没有可用 Vulkan 设备，Hy-MT2 1.8B 回退 7 线程 CPU 后等待超过 61 秒，最终加载超时。

可以确定失败发生在本机推理程序启动阶段，不能据此认定显存／内存不足，也不能据此认定某个驱动故障。任务管理器的 DirectX GPU 可见性不能证明同一进程的 Vulkan 初始化成功。目标电脑尚未提供原生错误，实际触发原因待新版错误记录确认。

## 已确认的实现缺陷及修正

- 原显卡探测只有 5 秒；超时、原生退出失败、确实没有设备均返回 null，界面一律显示“没有可用显卡”。现在使用可取消的 20 秒上限，区分超时、退出码、启动异常和正常空列表，保留已产生的部分输出。
- b10276 的动态 Vulkan 后端即使指定 `--device none -ngl 0` 仍被加载。CPU 子进程增加空 `GGML_VK_VISIBLE_DEVICES`，屏蔽 Vulkan 计算设备与 host buffer 初始化；仍会加载 Vulkan 后端、创建实例和枚举物理设备，不能称为完全不加载 Vulkan DLL。设置只作用于该子进程，不修改用户或系统环境。
- CPU 原先额外受 60 秒限制，现在使用整个启动操作的剩余预算；全部探测与尝试仍受原有 90 秒总上限约束，GPU 每次最多 30 秒，不无限重试。
- 原 `--log-disable` 加输出丢弃掩盖了启动原因。现在内存中仅保留有上限的启动输出，health 成功后立即清空并停止接受内容；取消不写故障日志。启动失败才在后台写 `ScreenshotData/Diagnostics/translation-startup-errors.log`，不可写则回退 `%LOCALAPPDATA%/SnapCut/Diagnostics`，提示实际路径。
- 错误包含模型、应用版本、系统、资源预算、设备探测及各次启动错误。每次最多约 32 Ki 字符，主文件 512 KiB 加一份轮换备份；不记录截图、翻译原文或译文，临时本机 API key 脱敏。原生进程退出时等待管道回调完成，避免漏掉最后一行错误。

运行库依据：

- [b10276 动态后端加载](https://github.com/ggml-org/llama.cpp/blob/b10276/ggml/src/ggml-backend-reg.cpp)
- [b10276 Vulkan 可见设备处理](https://github.com/ggml-org/llama.cpp/blob/b10276/ggml/src/ggml-vulkan/ggml-vulkan.cpp)

## 验证范围

开发机为 Intel Core Ultra 7 270K Plus、RTX 5070 Ti 与 Intel 核显，不是用户截图中的 GTX 1650 Ti。真实模型采用已安装的 Hy-MT2 1.8B Q4_K_M、TranslateGemma 4B Q4_K_M 和 b10276，不替换模型或降低精度。显卡与强制 CPU 分别验证英文到中文、中文到英文，首次加载、连续翻译、取消后恢复、空闲退出后重新加载，以及失败后进程退出。

首次验证：Hy-MT2 显卡加载 1748 ms、CPU 加载 963 ms；TranslateGemma 显卡加载 2272 ms、CPU 加载 1652 ms。均为该开发机已安装文件、短句测试，不代表目标机冷磁盘缓存或整页翻译耗时。四条路径均在空闲约 10.2–10.3 秒后实际退出原生进程。工作集和专用提交量记录在测试输出，非显存或峰值指标。

隔离的损坏模型测试还检查真实原生加载失败的退出码、最终错误行与子进程释放，不修改用户模型。4 GB 独显有余量／紧张预算沿用相关策略回归；缺少 GTX 1650 Ti 实机验证，不能将预算模拟当作驱动兼容性证明。

最终相关回归：36 项通过、3 项跳过（1 项真实桌面输入及 2 项另有环境开关的日文样本测试；本轮另外的 5 项真实原生运行测试已实际执行）。真实损坏模型留下退出码 `0x00000001`、`invalid magic characters` 和最终 `exiting due to model loading error`，并释放子进程与隔离文件。测试宿主日志中的版本 15.0.0 来自 testhost 的入口程序集；正式进程读取本次 SnapCut 应用版本。Release 编译未引入新警告，仍有既存 OCR／设置分析器提示。

本次仅修改 Windows。交付仍为 4.1.7-localtest，不作为正式更新发布。目标机请用同一轻量模型复测；如果仍失败，提供提示中实际路径的 `translation-startup-errors.log`，据此区分驱动初始化、模型读取、运行库加载或本机服务就绪问题。

## 最新交付

`installer/build-installer.ps1 -Version 4.1.7 -LocalTest` 的更新回归为 61 通过、1 个显式联网 Gitee 测试跳过。7z 完整性、13 个 ZIP 条目与本次发布目录逐文件 SHA-256、版本及双源更新清单校验通过。`installer/dist` 已替换同名 Windows 测试产物，Mac 产物未动。

| 文件 | 字节 | SHA-256 |
| --- | ---: | --- |
| SnapCut-Portable-4.1.7-win-x64.7z | 97031402 | 45FDA8AB7D039289A217823501B4519FAF726236A91CA2196601A63182D809E6 |
| SnapCut-Portable-4.1.7-win-x64.zip | 130947065 | 9EDB3C2ECD99EA1750F77E35C7786A14B95E4A743C4C2AA6A4A9C3150F0168F8 |
| SnapCut-Setup-4.1.7-win-x64.exe | 99767126 | 98689D6B23177306AD9ED4403075EA489D9ACD021F2D4E6932E719CE57B50A8E |
