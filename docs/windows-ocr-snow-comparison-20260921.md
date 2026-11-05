# Windows OCR 与 Snow Apps 实现对照（2026-09-21）

对照源码：mg-chao/snow-apps，提交 ef53fbb9304111af9a612cbedc8d92c0c2fdc529。Windows 版本保持 4.1.8；未复制 Snow 源码或将其运行时加入产品。用户要求改善这类海报艺术字的通用能力，不能只对“優雅／時尚／美尚”海报做内容特判。

## 已确认并修正

- Small 检测、识别及字典 SHA-256 与 Snow 相同（090f04ab…、6f327246…、b5f2bfe2…），Medium 文件也相同。差异不能归结为模型名称或文件大小。
- 原裁切继承了旧 PPv5 的顺时针旋转，PPv6 竖排应逆时针旋转；已按模型预设修正，并同步字符框逆变换。旧 v5 路径不变。同一 Medium 模型在海报竖列上从零碎错字变为完整竖列，不涉及固定文字或坐标。
- PPv6 识别仍沿用 v5 紧贴文字宽度及 Mitchell 采样。改用线性缩放、最小宽 320、32 对齐和归一化零值右填充；词框按有效内容宽度映射。旧 v5 保留原预处理。描边竖排“山川湖海”此前读成“山I湖海”，修正后微软雅黑、楷体、宋体的三组不同文字共 9 个组合均准确识别。
- 会话图优化由 EXTENDED 改为 ALL；inter-op 为 1，保留硬件相关的 intra-op/行并发预算和关闭空闲自旋。归一化直接写连续通道，消除逐像素 Tensor 多维索引分配，数学结果、通道顺序、行跨度不变。
- 完整高可信竖列跳过重复切分搜索；缺字或不确定结果保留补识别。没有降低识别阈值、替换模型或修改原始截图。
- 提前采纳 Medium 整图结果以减少切分的实验导致文字误合并，未通过回归，已撤回；不作为交付成果。

## Snow 实现与未完成范围

Snow 按宽高比排序后批量识别（默认 6 行），也支持 DirectML/CPU。SnapCut 本次仍使用 CPU 和硬件预算内的逐行并发，没有引入 DirectML，不能声称已使用用户 RX470 加速。Snow 实際后端用户不清楚；离线翻译的 Vulkan 检测与 OCR 后端不同。

使用 Snow 公开的 Windows 原生运行时 1.0.7（ZIP SHA-256 dd529a051862183a4c1782b973c28559d6c7abf37e09214af594bcd2102062e1）仅做本地 CPU 对照，读取同一模型和同一海报，不上传图片。开发机上 Small 两次约 0.195/0.184 秒，Medium 约 1.492/1.446 秒，仅为原生识别；其 Medium 结果仍有“優時美／雅尚尚”的读序及小字错误，不能声称该样本完全正确。SnapCut 完整服务还执行增强与版面恢复，耗时不能与该单次原生推理直接等同。

## 最终验证

开发机为 Intel Core Ultra 7 270K Plus，并非用户的 i5-7500。以下不代表目标机器保证：

- 52 项通过、3 项因缺少另一个私有艺术字／边界样本跳过。包含真实模型推理、三种字体、横竖排、间距、边距、字符数、黄色小字原图及编辑导出、坐标、像素不变和解码回归。
- 独立 Medium 的 9 个描边竖排组合全部正确，字符框覆盖顶部到末尾且位于原图内；通用布局另外覆盖 24 个字体／方向／间距／边距组合。
- 已找到用户确认的 727×940 原海报。原图及 80%/125% 缩放均覆盖预设的 12 个短语；这不等于逐字全对，其他小字仍有错误。
- 最终完整海报服务首次 6.707 秒、连续 6.835 秒、空闲退出重载 7.505 秒；80% 为 6.642 秒、125% 为 11.456 秒。还没有达到任意艺术字 0.5–1 秒的要求，不能报告目标机 20 秒问题已彻底解决。
- 海报工作进程总工作集 1167.1 MiB，峰值总工作集 1188.1 MiB，专用提交量 1342.1 MiB。已验证约 10 秒空闲实际退出、取消后退出、重载结果一致和取消后恢复；主程序不常驻 ONNX。
- 普通截图像素写入优化的独立对照曾在 3 线程预算下约改善 15–20%，不能将此数值外推到所有艺术字或把线程限制当作 i5 模拟。

小体积证据保存在 artifacts/snow-ocr-comparison/final-regression.log、orientation.json、snow-results.json 及 artifacts/ocr-direct-buffer-comparison.json。解压运行时、下载 ZIP、共享像素文件与过渡备份已清理。参考源码 artifacts/reference-snow-apps 保留一份，供尚未完成的目标机性能及批处理对照使用；用户原图、配置、模型和当前 Release 保留。

## 对照源码

- https://github.com/mg-chao/snow-apps/blob/ef53fbb9304111af9a612cbedc8d92c0c2fdc529/snow-crates/crates/rapid-ocr-rs/src/pipeline/image_ops.rs
- https://github.com/mg-chao/snow-apps/blob/ef53fbb9304111af9a612cbedc8d92c0c2fdc529/snow-crates/crates/rapid-ocr-rs/src/rec/recognizer.rs
- https://github.com/mg-chao/snow-apps/blob/ef53fbb9304111af9a612cbedc8d92c0c2fdc529/snow-crates/crates/rapid-ocr-rs/src/runtime/session.rs
- https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html
