# SnapCut · 简截

[简体中文](#简体中文) | [English](#english)

[![GitHub release](https://img.shields.io/github/v/release/jiuwanzi-hui/Screenshot?style=flat-square&label=release)](https://github.com/jiuwanzi-hui/Screenshot/releases/latest)
[![GitHub downloads](https://img.shields.io/github/downloads/jiuwanzi-hui/Screenshot/total?style=flat-square&label=downloads)](https://github.com/jiuwanzi-hui/Screenshot/releases)
[![GitHub issues](https://img.shields.io/github/issues/jiuwanzi-hui/Screenshot?style=flat-square)](https://github.com/jiuwanzi-hui/Screenshot/issues)
[![GitHub stars](https://img.shields.io/github/stars/jiuwanzi-hui/Screenshot?style=flat-square)](https://github.com/jiuwanzi-hui/Screenshot/stargazers)
[![Windows 10 | 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?style=flat-square&logo=windows11)](https://www.microsoft.com/windows)
[![macOS preview](https://img.shields.io/badge/macOS-preview-7657FF?style=flat-square&logo=apple)](src/SnapCut.Mac/README.md)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/)
[![License: Personal Non-Commercial](https://img.shields.io/badge/license-personal%20non--commercial-D94B64?style=flat-square)](LICENSE)
[![Gitee mirror](https://img.shields.io/badge/Gitee-国内镜像-C71D23?style=flat-square&logo=gitee)](https://gitee.com/wwangyunhui/screenshot)

![SnapCut icon](src/Screenshot.App/Assets/Screenshot.png)

## 简体中文

SnapCut（简截）是一款以 Windows 10/11 正式版为主的轻量截图工具，取"简洁"与"直截了当"之意。它把区域截图、区域视频录制、长截图、标注、OCR、钉图和可选翻译放在同一条紧凑工作流中，并优先在本机完成处理。macOS 菜单栏预览版已进入源码，当前支持区域截图、长截图、全局快捷键、预览、复制、保存和截图历史。

> **许可声明：本项目不是 OSI 定义的开源软件。** 源代码仅授权自然人用于个人、非商业的学习、运行和修改；未经版权所有者书面许可，禁止任何商业、盈利、企业内部业务用途，以及安装包、可执行文件或其他二进制版本的再分发。为个人学习或向本项目贡献而建立的非商业源码分支，必须保留完整许可证和版权声明。详见 [LICENSE](LICENSE)。第三方组件仍适用各自的原许可证。

### 主要优点

- **菜单、下拉框和悬浮提示也能截到**：快捷键触发时先冻结当前桌面画面，尽量保留右键菜单、通知区域菜单、下拉列表、悬浮提示等容易因失去焦点而消失的临时界面，再进入框选和编辑。
- **截图后直接调整和编辑**：鼠标悬停到其他软件时可自动吸附整个窗口，单击直接选取，也可继续拖动自由框选；移动选区时底图保持固定，框内明亮、框外变暗。编辑后仍可用八个控制点扩展或缩小选区，边缘不会越过已有标注；已经放置的矩形、箭头、文字和表情可直接点击选中，再调整位置、大小、端点或边角，无需重新选择工具；并提供画笔、马赛克、颜色、线宽、撤销和重做。
- **受控长截图**：普通截图框选后点击工具栏的 `↕` 图标；单击选区可匀速向下采集，等待开始时双击则可先向上采集。程序发送固定节拍的细粒度鼠标滚轮消息，鼠标指针可自由移动且不会被锁定。单击暂停会先停稳并补齐最后视口，再允许继续；第一方向内双击会停稳后快速返回初始位置且不重复写入，随后自动向相反方向匀速拼接。到达真实边界后只停止滚动并保留当前结果，等待用户编辑、确认或取消；实时预览持续显示完整结果。
- **区域视频录制**：普通截图框选后点击摄像机图标，冻结截图和遮罩立即关闭，真实桌面保持可点击、可播放和可操作；独立控制条支持开始、暂停、继续、结束和计时，控制条会从捕获中排除，选区提示框绘制在录制区域之外。可选择 H.264/H.265、24/30/60 FPS、系统声音、麦克风，以及不显示/显示鼠标/显示键盘/同时显示键盘鼠标，设置会自动记住；视频保存位置可在常规设置中单独指定。录屏期间仍可使用 SnapCut 截图，再次启动录屏会明确提示正在录制。当前区域录制要求选区位于同一块显示器内。
- **悬浮按钮与多屏截图**：可开启只显示 SnapCut 图标的悬浮按钮，拖到任意显示器边缘会自动吸附、部分隐藏并降低透明度，鼠标移入后完整展开；位置在退出、重启和更新后继续保留。单击动作可配置为上次区域直接截图、显示上次选区、区域截图、视频录制、长截图、钉图或全部屏幕截图，悬浮菜单也可直接打开常用功能。全部屏幕截图会一次捕获 Windows 虚拟桌面，按“显示设置”中的物理排列组合所有屏幕，以白色填充空缺位置并绘制细分界线。
- **本地 OCR 与图片文字选择**：使用 Windows 本地 OCR 引擎，图片不需要上传；识别后可直接在截图、OCR 结果窗口和钉图上拖选文字并通过原生 Unicode 剪贴板复制，译文覆盖完成后同样可以选择和复制，识别语言取决于系统已安装的语言包。
- **在线或完全离线的多语言翻译**：翻译快捷键可在框选后自动完成 OCR 与翻译；设置页直接显示在线大模型与本机 Bergamot 模型的优先顺序、真实可用状态和悬停原因，用户可上下调整，上一项失败时自动使用下一项。在线接口可选择 OpenAI、DeepSeek、通义千问、Claude、Gemini、Grok 等内置厂商或自定义兼容地址，并自动验证 API、模型列表和当前模型。翻译会自动识别截图文字的源语言；离线模型使用本机 CLD3 检测语言，并支持 Mozilla 当前模型清单里的多语言互译。离线下载会显示流量、安装占用、磁盘余量和实际目录，断流时自动重试；模型文件被移动或删除后，设置页回到前台会立即重新核验。文件保存在 `SnapCut.exe` 旁的 `TranslationModels`，安装版或免安装版更新都会保留该目录。API Key 使用当前用户 DPAPI 加密保存；翻译结果可切换原文/译文并覆盖到最终图片。
- **高效的日常操作**：支持全局快捷键、系统托盘、截图历史、钉图、开机启动和完整的深浅色主题；设置、编辑截图、OCR 结果、截图历史和图片查看窗口会分别记住上次的位置、大小及最大化状态，显示器变化后会自动把窗口拉回可操作区域。设置窗口每次从小窗、通知区域或快捷键重新显示到前台时都会检测更新，有新版本会在“版本更新”入口显示提示，并发检测 Gitee 国内源和 GitHub 国际源，自动采用可用更新源，下载失败时切换备用源。“版本更新”页同时显示各正式版本的发布时间与更新内容，可选择 2.0.0 及之后的已校验版本进行更新或回退；更早版本因程序改名只提供发布页手动安装。安装版和免安装版覆盖时都会保留 `ScreenshotData`。首次运行默认同时显示任务栏图标和通知区域图标，避免用户找不到程序入口。
- **数据跟随程序目录**：安装版与免安装版都把设置、加密凭据、历史和默认截图保存在 `SnapCut.exe` 旁的 `ScreenshotData`，不会把个人配置提交到源码仓库。

### 下载与安装

下载最新的 x64 自包含版本。2.3.0 起发布附件统一使用 `SnapCut-Setup-*` 和 `SnapCut-Portable-*`；同时保留旧清单入口，2.1.0 及之后版本仍可在程序内在线更新：

- [安装版 SnapCut Setup 3.7.8（GitHub）](https://github.com/jiuwanzi-hui/Screenshot/releases/latest/download/SnapCut-Setup-3.7.8-win-x64.exe)
- [免安装版 SnapCut Portable 3.7.8（GitHub）](https://github.com/jiuwanzi-hui/Screenshot/releases/latest/download/SnapCut-Portable-3.7.8-win-x64.zip)
- [安装版 SnapCut Setup 3.7.8（Gitee 国内镜像）](https://gitee.com/wwangyunhui/screenshot/releases/download/v3.7.8/SnapCut-Setup-3.7.8-win-x64.exe)
- [免安装版 SnapCut Portable 3.7.8（Gitee 国内镜像）](https://gitee.com/wwangyunhui/screenshot/releases/download/v3.7.8/SnapCut-Portable-3.7.8-win-x64.zip)

免安装版解压后直接运行 `SnapCut.exe`，不需要安装或预先配置 .NET 或 Visual C++ 运行库。安装版和免安装版的数据都保存在各自程序目录的 `ScreenshotData` 中；移动或卸载程序前请按需备份该目录。2.8.3 延续现有数据布局，并会在发现 1.0.0 的 `%LocalAppData%\Screenshot` 旧数据时尝试迁移到新位置，迁移成功后删除旧目录。

3.7.8 在 3.7.5 的截图、钉图和录屏框选稳定性修复基础上，补充预设截图区域管理：支持最多五个区域、悬浮编号面板、区域高亮、直接截图、右键编辑、删除和一键清空，并改善透明预览、主题适配及面板外关闭行为。快捷键首次进入截图的预热流程、原生选区与遮罩同步更新、标注编辑和复制行为继续修正；正式 Release 不启用输入时序调试日志。

3.7.5 集中修复高回报率鼠标下截图、钉图和录屏框选的跟手性、遮罩残留、双边框和切换闪烁；原生选区、尺寸标签、遮罩及标注预览的更新链路进一步统一。修复修饰键和鼠标左右键快捷键对普通应用、托盘菜单和文件右键菜单的误拦截，避免 Alt/Ctrl 被系统输入钩子吞掉；录屏键盘/鼠标提示、文字编辑与复制、标注工具独立颜色和线宽持久化也同步修正。翻译在线模型按配置顺序失败回退，设置页启动后先显示上次状态并在后台重新检测。

3.7.0 完善截图、钉图和录屏工具栏的布局、图标缩放与交互状态，录屏摄像头预览、历史记录及快捷键配置进一步可靠。翻译设置支持手动模型测试、模型列表刷新和多语言离线包官方备用地址；离线模型下载会在连接中断后按实际断点续传，避免切换下载源时重复拼接文件并在完成后重新下载。表格复制入口改为独立工具，改善合并单元格、背景表格和数字日期的识别保留。启动设置可选择最小化启动并给出主题化提示。

3.6.1 修复 ToDesk 等远程控制环境中截图、钉图和录屏框选拖动掉帧、边框落后鼠标的问题：连接真实显示输出时恢复 WPF GPU 合成，仅在合盖、无显示器或只有虚拟显示输出时使用软件渲染，继续避免白屏。拖动中的分辨率标签改为固定布局，减少每帧重复测量带来的界面开销。

3.6.0 增加录屏摄像头实时预览，支持实体、虚拟摄像头和多麦克风选择；摄像头画面可在录制区域内移动、四角等比例缩放并双击恢复，设备选择会跨录制保留，无首帧时自动重连。录屏框选恢复动态画面，工具栏和输入轨迹交互进一步优化。截图、钉图和录屏的框选边框更跟手，拖动时同步显示分辨率并消除旧画面残留。整图翻译改进分区并行、服务优先级与失败回退，提升复杂页面的速度和覆盖完整性；设置页新增可在线替换二维码的“交流联系”入口。

3.5.0 为区域录屏增加与普通截图一致的实时标注工具栏，支持矩形、椭圆、直线/曲线箭头、画笔、文字、序号、表情和马赛克；标注可选中后移动、缩放、调整颜色与粗细，常用颜色和工具样式会跨录制保留。录屏支持按键提示、主题色鼠标渐隐轨迹、暂停、取消及结束后自动打开录屏历史，同时修复首次录屏初始化卡顿、重复画面、无显示器环境兼容、椭圆/画笔控制点和颜色滑块交互问题。

3.4.2 修复整图翻译覆盖层的段落布局：同一段 OCR 行统一字号与行距，合并重叠区域并去除重复译文，减少多栏页面中文字上下覆盖、字号忽大忽小的问题。截图内容已经是目标语言时不再误报翻译失败。设置窗口改用兼容的软件合成路径，修复笔记本合盖、主机未连接显示器及部分远程控制环境中窗口内容白屏的问题。

3.4.1 优化整图翻译：使用已安装的离线语言路由进行批量翻译，避免无必要的在线请求和逐行重试；混合语言页面按实际文本占比选择源语言，URL、侧栏短词和技术标识不会导致整张译文被丢弃。移除整图翻译的全局 9 秒硬截断，改为按批次超时并保留已完成译文；同时修复多栏网页和长页面翻译失败、段落布局异常的问题。

3.4.0 优化全局输入和截图性能：录屏初始化及内存整理移出界面线程，减少录屏快捷键卡顿和静态内存缓慢上涨；移除输入钩子中的同步磁盘日志与阻塞式菜单截图，延迟截图任务增加代际校验，避免旧画面残留到下一次截图。修复快捷键设置页自动进入录入、资源管理器和托盘菜单右键截图，以及鼠标钉图短按与长按的判定：短按保留原生点击，长按才拦截并触发钉图。

3.3.0 完善截图与钉图的连续操作：工具栏布局、形状和箭头变体、常用颜色等偏好会跨重启保留；钉图重开后主按钮、实际绘制工具和下拉高亮保持一致。历史记录可配置数量与保留天数，并在关闭永久保留前先确认新的保留策略。截图界面未选区时按一次 `Esc` 即可退出，空白区域点击行为更清晰；长时间闲置后的首次快捷键唤醒也减少了主线程阻塞。隐私打码补充更多敏感内容识别与确认流程。正式版长截图只记录错误诊断，`crash.log` 限制为 10 MiB，超限后自动重新开始记录。

2.8.3 完善截图取色模式：支持放大镜、快捷复制提示、主题化颜色面板、色相/饱和度/亮度/透明度调节和 HEX 输入；常用颜色槽支持逐个保存覆盖，并兼容回显旧版 Windows 调色板配置。取色和颜色调节流程减少重复保存，提升拖动流畅度。

2.8.1 支持将 `F1-F24` 设为不带修饰键的单键快捷键。修复截图翻译中英文未翻译、字符图标误识别为数字、中文或产品标识被错误覆盖的问题；优化 OCR 文字框与逐行译文的对应关系。离线翻译未产生有效译文时会自动回退到下一翻译方式，避免把原文返回误判为成功。

2.8.0 新增截图与录屏统一历史查看、视频重命名/排序/定位、长图裁剪和多套独立主题；截图框选后可直接 `Ctrl+C` 完成复制，OCR 文字支持跨行选择，标注可选中后按 `Delete` 删除。内容识别改为按需运行：二维码保持轻量自动检测，文字 OCR 与表格复制由用户点击触发；可选高质量 OCR 与本地大模型支持断点下载、状态校验和超时回退。翻译、OCR 的布局匹配、取消响应和自适应线程预算进一步完善，降低不同配置电脑上的卡顿和 CPU 峰值。长截图恢复为选区内单击向下、双击向上后再开始受控滚动，不再使用物理滚轮采集。发布包继续自包含 .NET，并额外内置录屏所需的 Visual C++ x64 运行库，避免干净系统因缺少运行库而无法录屏。

2.7.1 修复 2.7.0 单文件正式包内嵌的 C++/CLI 录屏组件在部分环境无法加载、导致区域录制无法启动的问题。录屏组件现在作为独立文件随程序一同分发且无需用户额外安装，构建流程会在生成下载包前强制检查该文件；区域录制异常没有系统消息时也会显示具体异常类型，便于诊断。

2.7.0 新增区域视频录制，可配置 H.264/H.265、24/30/60 FPS、系统声音、麦克风和键盘/鼠标输入提示，并提供独立快捷键、通知区域及悬浮菜单入口；录屏控制条保持低遮挡，录制完成显示保存提示，录屏期间仍可正常截图。新增可配置悬浮按钮，支持记忆多屏位置、边缘吸附、低透明度收起、上次区域截图、区域截图、录屏、长截图、钉图和全部屏幕截图；全部屏幕截图按 Windows 显示器坐标一次捕获并组合所有屏幕。鼠标快捷键完善修饰键、侧键和可配置长按直接框选，同时避免 `Ctrl+左键` 多选冲突及首次截图穿透。窗口位置、托盘菜单、开机启动和多屏恢复逻辑进一步加固。macOS 菜单栏预览版源码加入区域截图、长截图、全局快捷键、预览、历史和权限引导，但尚不发布未签名二进制。

2.6.1 修复版本历史在 Gitee 和 GitHub 公共 API 同时限流时无法显示的问题。历史列表现在优先读取不消耗 API 配额的静态清单，网络不可用时使用程序内置清单或上次成功缓存；临时刷新失败也不会清空已经显示的版本。安装包和免安装包均内置正式版本清单，已校验版本的更新与回退下载仍使用双源地址和 SHA-256 校验。

2.6.0 在设置窗口新增“打赏支持”页面，内置微信扫码打赏二维码，用于支持 SnapCut 的持续维护、功能升级和 macOS 版本开发。本版本同时正式加入个人非商业许可证声明，许可证会随安装版和免安装版一同分发。

2.5.1 修复 `Ctrl+鼠标左键` 与资源管理器多选文件的冲突：左、中、右键及其修饰键组合现在统一采用可配置的长按触发，短按与移动拖拽继续交给当前软件；达到时长后仍可保持按住并直接拖动截图框。开机启动状态现在会精确校验完整启动命令，在线更新时若用户已经启用开机启动，也会将旧程序路径刷新为当前安装路径，避免更新后勾选状态失效。

2.5.0 新增完整的鼠标快捷键：可单独使用鼠标后退键、前进键，或组合 `Ctrl`、`Alt`、`Shift` 与鼠标左/中/右/侧键；单独左、中、右键采用长按触发，侧键可选择即时或长按触发，长按时间可在 `300–2000 ms` 调整。使用左键或右键触发区域截图、翻译、钉图或长截图时，可保持按住并直接拖动框选，第一次松开即完成选区。截图会话期间会暂停新的鼠标快捷键，完成、取消或关闭时强制释放鼠标捕获，避免点击穿透、重复触发或左键被持续占用。快捷键录制期间会拦截鼠标输入，不会误触其他软件。

2.4.0 将原“文字识别”快捷键升级为一键翻译：框选后自动完成本地 OCR、按优先级翻译并显示覆盖结果，旧快捷键配置保持兼容。在线服务新增自定义接口及 OpenAI、DeepSeek、通义千问、Claude、Gemini、Grok 等 14 个内置厂商，切换厂商时自动填入官方兼容地址并按厂商独立保存 API Key。翻译优先项会用“可用/不可用”小标显示实时状态，悬停可查看未配置 API、无法获取模型、当前模型无效或离线模型未下载等原因；设置窗口回到前台会重新验证在线模型和本地文件。设置页输入控件不再截获页面滚轮，刷新模型列表也不会清空当前选择。

2.3.1 将在线大模型与离线模型改为可排序的翻译优先级，当前方式请求失败时自动尝试下一项。大型离线模型下载增加连接/读取停滞超时和自动重试，保留已经校验完成的语言方向，并加固临时文件占用、拒绝访问和只读目录的清理；模型已经安装成功但旧临时目录未能立即清理时，不再误报整次安装失败。安装版和免安装版覆盖更新都会保留 `TranslationModels`，技术路径与文件名也不会再被误送去翻译。

2.3.0 新增在线大模型与 Bergamot 本机模型两种翻译方案，可自动识别源语言并翻译到用户选择的目标语言；离线包下载前会显示流量、安装占用、磁盘余量和安装位置。设置页新增正式版历史、发布时间、更新内容及已校验版本的更新/回退入口，每次重新显示到前台都会检查新版本。快捷键录制会在应用内截获按键，避免录制 `Alt+A` 等组合时触发其他软件，并修复免安装版的快捷键与主题保存。普通截图、长截图及文字结果窗口统一完善深浅色样式和窗口位置/大小记忆；长截图继续修正初始视口、反向扩展与重复纹理场景的拼接稳定性。发布包从本版起改用 `SnapCut-` 文件名，主清单与旧名兼容清单指向同一套附件。

2.2.2 修正设置侧栏的新版本提示：检测到更新时，“版本更新”文字直接替换为亮色“有新版本”，悬停可查看目标版本；没有更新时恢复原文字和导航颜色。移除会挤出侧栏、只显示一个“有”字的独立徽标，并加入亮色、恢复状态及侧栏边界回归。

2.2.1 修复长截图在暂停恢复时偶尔接受异常大位移、插入重复内容的问题；回到初始视口后，改用更严格且连续一致的图像证据再开始反向扩展，避免周期性代码行被错误拼入、压缩或缺失。诊断日志同时补充重叠行数、置信度、横向偏移和预期位移，便于后续精准定位边缘场景。

2.2.0 允许直接点击已经放置的矩形、箭头、文字和表情进行二次编辑，可继续调整位置、大小、端点和边角，并为可选择、放置和编辑状态提供对应鼠标指针；矩形只在精准边界范围内响应选择，不会阻挡在内部继续放置其他标注。设置窗口每次重新显示到前台都会自动检测更新，并新增柔和的浅色渐变与深青亮色渐变配色。截图工具栏改用大小统一的镂空矢量图标（保留原有表情图标）。长截图进一步修复初始视口接缝、暂停收尾、返回后反向扩展以及周期性代码行中的整行缺失和压缩。

2.1.0 重构长截图为固定节拍的受控滚轮流程：支持单击开始、暂停和继续，双击返回初始位置后反向扩展，鼠标可自由移动；拼接锚点绑定截帧瞬间的输入位置，修复暂停、回程及周期性代码行场景中的缺行、重影、错误边界和永久校准。长图查看器同时支持适应窗口、`Ctrl + 鼠标滚轮` 缩放和无手柄拖动。此版本还加入跨平台拼接核心及 macOS 命令行原型，Windows 功能与数据目录保持兼容。

2.0.0 将软件更名为 SnapCut（简截）并启用全新图标；重写长截图拼接内核（链式顺序匹配、采样背压、DwmFlush 对齐拷屏、边界重锚），任意速度与来回滚动都能稳定拼接；实时预览改为全局整图并随图增高；后台静态内存降至约 25MB；表情贴纸升级为 88 个系统原版彩色表情并支持长截图编辑；箭头改为微信式锥形实心样式；新增应用级异常兜底，异常记录到 `ScreenshotData\Diagnostics\crash.log` 而不再闪退。**免安装版从 1.3.2 及更早版本升级时**：由于程序文件名由 Screenshot.exe 更名为 SnapCut.exe，旧版内置更新器无法自动完成本次替换，请手动下载新版免安装包解压使用（可把旧目录的 `ScreenshotData` 拷贝过去保留数据）；安装版用户仍可在程序内一键自动更新。

1.3.0 是首个支持在线覆盖更新的版本；1.3.1 起同时提供 Gitee 国内源和 GitHub 国际源。左侧“版本更新”页面会并发检测两个来源，采用先返回的有效清单，下载失败时自动换源，并严格校验文件大小与 SHA-256。安装版静默覆盖原目录，免安装版安全替换程序文件，两种方式都保留 `ScreenshotData` 并自动重启。由 1.2.0 或更早版本升级时需手动安装一次 1.3.1，之后即可在程序内更新。

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
| 视频录制 | 未设置 |
| 翻译 | `Ctrl+Alt+O` |
| 钉图 | `Ctrl+Alt+P` |
| 打开设置 | `Ctrl+Alt+,` |

所有快捷键都可以在设置中修改或清空。录入时既可按键盘组合，也可直接按鼠标后退键、前进键，或使用 `Ctrl`、`Alt`、`Shift` 及其组合加鼠标左/中/右/侧键。鼠标左键、中键、右键无论是否带修饰键都按长按方式触发；未达到时长的短按和移动拖拽维持原操作，因此 `Ctrl+左键` 仍可正常多选文件。鼠标后退键和前进键默认按下触发，也可在设置中改为必须按满长按时间，未达到时间的短按仍执行原本的后退或前进操作。长按时间可在 `300–2000 ms` 调整，默认 `700 ms`。使用左键或右键触发区域截图、翻译、钉图或长截图时，可以继续按住并直接拖动框选，松开触发键即可完成选区；截图会话期间会暂停识别新的鼠标快捷键，结束或取消后自动恢复，避免框内点击再次触发截图。含 `Win` 键的组合仍由 Windows 保留。

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

安装包和免安装包使用 `SnapCut-` 前缀。构建脚本同时生成主清单 `SnapCut-Update.json` 与兼容入口 `Screenshot-Update.json`，四个文件都输出到 `installer/dist/`，两份清单还会同步到 `updates/` 供 Gitee 固定地址读取。发布时四个文件需要同时上传到 GitHub 与 Gitee 的同版本 Release；两份清单都指向同一套 `SnapCut-` 安装包，不会重复上传大文件。项目架构见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)，实现进度见 [PLAN.md](PLAN.md)。

### macOS 预览版

macOS 端与 Windows WPF 前端完全隔离，复用平台无关的长截图拼接核心。无参数启动
`snapcut` 后会驻留菜单栏，默认使用 `⌘⇧A` 区域截图、`⌘⇧S` 长截图；设置页可修改
快捷键、申请屏幕录制/输入监控权限并打开最近截图。构建 `.app`：

```powershell
pwsh ./build-macos.ps1 -Runtime osx-arm64 -Version 0.1.0
# Intel Mac 使用 -Runtime osx-x64
```

具体功能、权限、签名和真实 Mac 验收边界见
[SnapCut.Mac 说明](src/SnapCut.Mac/README.md)。当前仓库尚未提供经过签名和 Apple
公证的 macOS 正式下载包。

---

## English

SnapCut is primarily a lightweight Windows 10/11 capture utility. It combines region capture, scrolling capture, annotation, OCR, pinned images, and optional translation in one compact workflow while keeping processing local whenever possible. A source-level macOS menu bar preview now provides region capture, scrolling capture, global hotkeys, preview, clipboard/save actions, and capture history.

> **License notice: this project is not open-source software as defined by the OSI.** The source is licensed to individuals solely for personal, non-commercial study, use, and modification. Commercial use, revenue-generating use, internal business use, and redistribution of installers, executables, or other binary builds require prior written permission from the copyright holder. Non-commercial source forks created for personal study or contributing to this project must retain the complete license and copyright notice. See [LICENSE](LICENSE). Third-party components remain under their respective licenses.

### Why SnapCut

- **Capture menus, drop-downs, and tooltips**: the hotkey freezes the visible desktop before focus-sensitive UI can close, preserving transient surfaces such as context menus, notification-area menus, drop-down lists, and hover tooltips for selection and editing.
- **Adjust and edit before finishing**: hover over another application to snap to its full window and click to select it, or drag to make a free-form selection. The desktop stays fixed while the selection moves, with a bright interior and dimmed exterior. After annotating, all eight handles remain available while protected bounds prevent existing ink from being cropped. Existing rectangles, arrows, text, and emoji can be clicked directly and then moved or resized, including arrow endpoints and rectangle corners, without reselecting a tool. Freehand drawing, mosaic, colors, stroke widths, undo, and redo remain available.
- **Controlled scrolling capture**: make a normal region selection, click the `↕` toolbar icon, then click inside the region to scroll downward slowly; double-click before starting to capture upward first. SnapCut sends fine-grained wheel messages at a fixed cadence without locking the pointer. Click to pause or resume; double-click during the first leg to settle, return quickly to the initial viewport without rewriting pixels, then continue in the opposite direction. At a real boundary scrolling stops while the current result remains available for editing, confirmation, or cancellation. The live preview always shows the complete result.
- **Region video recording**: after selecting a normal capture region, click the camera icon to dismiss the frozen screenshot and dimming overlay, leaving the live desktop fully interactive. A separate controller provides start, pause, resume, stop, and elapsed time; the controller is excluded from capture and the region frame is drawn outside the recorded pixels. Choose H.264/H.265, 24/30/60 FPS, system audio, microphone, and no input overlay/mouse/keyboard/both; preferences are retained. The video directory is configurable separately. Screenshots remain available while recording, and attempting to start another recording shows an explicit notice. The current recorder requires a region contained by one display.
- **Floating button and multi-display capture**: an optional icon-only floating button docks to any monitor edge, partially hides at low opacity, expands on hover, and retains its position across restarts and updates. Its click action can repeat the last region immediately, reopen that selection, start a region capture, record video, capture a scrolling page, pin an image, or capture every display. All-display capture takes one virtual-desktop snapshot, preserves the Windows display arrangement, fills unused layout gaps with white, and draws subtle display boundaries.
- **Local OCR with selectable image text**: uses the Windows OCR engine without uploading images, then overlays selectable text directly on captures, OCR result windows, and pinned images, copying it through the native Unicode clipboard. Translated overlays are selectable and copyable as well. Available languages depend on installed Windows language packs.
- **Online or fully offline multilingual translation**: the translation hotkey runs local OCR and translation immediately after region selection. Settings show a sortable priority, live available/unavailable badges, and hover reasons for online and local Bergamot providers. Online configuration includes custom endpoints plus built-in OpenAI, DeepSeek, Qwen, Claude, Gemini, Grok, and other vendor definitions, with automatic endpoint and model validation. Source languages are detected automatically; offline translation uses local CLD3 and Mozilla Bergamot models for multilingual routes. Downloads show transfer size, installed footprint, free space, and destination, retry interrupted transfers, and are revalidated whenever Settings returns to the foreground. Models stay in `TranslationModels` beside `SnapCut.exe` and survive installed or portable updates. API keys are protected by per-user Windows DPAPI, and translated overlays remain reversible and selectable.
- **Fast daily workflow**: configurable global hotkeys, system tray controls, capture history, pinned images, startup behavior, and complete light/dark themes. Settings, image editors, OCR results, capture history, and image viewers each remember their last position, size, and maximized state; if the monitor layout changes, windows are brought back into a reachable work area. Whenever Settings returns to the foreground from the compact window, notification area, or hotkey, it checks for updates and highlights the Update entry when a newer release exists. Gitee and GitHub provide mirrored update sources with automatic download fallback. The Update page also lists formal releases with publication times and release notes; verified packages from 2.0.0 onward can be selected for an update or rollback, while older renamed builds link to manual installation. Installed and portable replacements preserve `ScreenshotData`. New installations show both taskbar and notification-area icons by default so the application remains easy to find.
- **Data stays with the application**: installed and portable builds keep settings, encrypted credentials, history, and default captures in `ScreenshotData` beside `SnapCut.exe`. Personal configuration is excluded from the repository.

### Download and install

Download the latest self-contained x64 build. Release assets use `SnapCut-Setup-*` and `SnapCut-Portable-*` starting with 2.3.0. The legacy manifest endpoint remains available so versions 2.1.0 and later continue to update in-app:

- [SnapCut Setup 3.7.8 (GitHub)](https://github.com/jiuwanzi-hui/Screenshot/releases/latest/download/SnapCut-Setup-3.7.8-win-x64.exe)
- [SnapCut Portable 3.7.8 (GitHub)](https://github.com/jiuwanzi-hui/Screenshot/releases/latest/download/SnapCut-Portable-3.7.8-win-x64.zip)
- [SnapCut Setup 3.7.8 (Gitee China mirror)](https://gitee.com/wwangyunhui/screenshot/releases/download/v3.7.8/SnapCut-Setup-3.7.8-win-x64.exe)
- [SnapCut Portable 3.7.8 (Gitee China mirror)](https://gitee.com/wwangyunhui/screenshot/releases/download/v3.7.8/SnapCut-Portable-3.7.8-win-x64.zip)

Extract the portable archive and run `SnapCut.exe`; neither installation nor a preinstalled .NET or Visual C++ runtime is required. Both packages store their data in `ScreenshotData` under their respective application directories, so back up that directory before moving or uninstalling the application. Version 2.8.3 retains the existing data layout and attempts to migrate legacy 1.0.0 `%LocalAppData%\Screenshot` data when found, removing the old directory after a successful migration.

Version 3.7.8 adds preset screenshot region management with up to five saved regions, a numbered floating panel, hover highlighting, direct capture, right-click editing, deletion, clear-all, transparent themed previews, and outside-click dismissal. It also carries forward the capture warm-up, native selection/mask synchronization, annotation editing, and copy fixes; timing traces remain disabled in Release builds.

Version 3.7.5 fixes high-report-rate pointer lag, stale masks, duplicate borders, and transition flicker while moving screenshot, pin, and recording selections. Native selection, size, mask, and annotation preview updates now share one path. Modifier and mouse-button shortcuts no longer swallow Alt/Ctrl or disrupt regular applications, tray menus, or Explorer context menus; recording keyboard/mouse hints, text editing and copy, and per-tool color/width persistence are corrected. Online translation providers now fall back in configured order, and the settings page shows the last known availability before refreshing in the background.

Version 3.6.1 fixes dropped frames and pointer lag while moving screenshot, pin, and recording selections through ToDesk and similar remote-control tools. WPF GPU composition is restored when a physical display output is available, while lid-closed, display-less, and virtual-display-only hosts retain the software-rendering fallback that prevents blank windows. The live resolution badge now uses a stable layout to avoid repeated measurement work during pointer movement.

Version 3.6.0 adds a live camera preview to region recording with physical and virtual camera support plus selectable microphones. The camera frame stays inside the recording region, moves and resizes at its original aspect ratio, restores on double-click, remembers the selected device, and reconnects automatically when the first frame stalls. Region recording is live again while selecting, and toolbar/input-overlay interaction is smoother. Screenshot, pin, and recording selections now track the pointer more closely, show resolution while dragging, and avoid stale-frame remnants. Full-image translation improves parallel region processing, provider priority, and fallback coverage on complex pages. Settings also gains a Contact page whose QR image can be replaced remotely.

Version 3.5.0 adds a live annotation toolbar to region recording with the same rectangle, ellipse, straight/curved arrow, brush, text, number, emoji, and mosaic tools used by screenshots. Annotations can be selected, moved, resized, recolored, and restyled, while recent colors and tool variants persist between sessions. Recording now supports keystroke hints, a theme-colored fading mouse trail, pause, cancel, and automatic opening of video history after completion. It also fixes first-recording startup stalls, duplicated frames, headless-display compatibility, ellipse/brush resize handles, and color-slider interaction.

Version 3.4.2 fixes full-image translation overlay layout by giving each OCR paragraph one font size and line height, merging overlapping regions, and removing duplicate translated text. Screenshots that are already in the target language no longer report a translation failure. WPF windows use a compatibility rendering path to prevent blank content when a laptop lid is closed, no physical display is connected, or certain remote-control tools are active.

Version 3.4.1 improves full-image translation: installed offline routes are used for batched translation without unnecessary online requests or per-line retries; mixed-language pages choose the source language from the actual text balance, and URLs, sidebar labels, and technical identifiers no longer discard an otherwise complete translation. The global nine-second cutoff is removed in favor of per-batch timeouts that preserve completed translations, and multi-column/long-page overlay layout is corrected.

Version 3.4.0 improves global input and capture performance: recording initialization and memory cleanup no longer block the UI thread; synchronous hook logging and blocking menu captures are removed; delayed snapshots use generation checks so stale frames cannot leak into the next capture. Hotkey settings no longer enter capture mode on restored focus. Explorer and tray context-menu capture is fixed, and mouse pinning now preserves native short clicks while intercepting only a long press.

Version 3.3.0 improves continuity across capture and pinned-image workflows. Toolbar layout, shape and arrow variants, and custom colors now survive restarts; reopening a pinned image keeps the button icon, selected drawing tool, and dropdown state aligned. History supports configurable item and day retention, with a choice of the new retention policy before permanent retention is turned off. A single `Esc` exits an unselected capture, background clicks behave consistently, and first hotkey activation after a long idle period avoids unnecessary UI-thread blocking. Privacy redaction recognizes more sensitive text and has a clearer confirmation flow. Release builds keep only scrolling-capture errors, while `crash.log` is capped at 10 MiB and starts fresh after reaching that limit.

Version 2.8.3 improves screen color picking with a magnifier, copy confirmation, themed color controls, HSV/alpha sliders, HEX input, and individually saved palette slots compatible with legacy Windows color settings. Color adjustments avoid repeated persistence work for smoother dragging.

Version 2.8.1 allows `F1-F24` to be assigned as single-key shortcuts without modifiers. It fixes untranslated English, icon glyphs being misread as digits, and Chinese or product identifiers being incorrectly overwritten in screenshot translation, while improving OCR box alignment and line-by-line translated overlays. An offline provider that returns no effective translation now falls back to the next provider instead of treating unchanged source text as success.

Version 2.8.0 adds unified screenshot and recording history, video rename/sort/location actions, scrolling-capture cropping, and several independent themes. A selected capture can now finish directly with `Ctrl+C`; OCR text supports multiline selection, and selected annotations can be deleted with `Delete`. Content recognition is now demand-driven: QR detection remains automatic and lightweight, while OCR and table copying run only when requested. Optional high-quality OCR and local large translation models support resumable downloads, file validation, timeouts, and fallback. Translation/OCR layout matching, cancellation, and adaptive thread budgets reduce stalls and CPU spikes across different hardware. Scrolling capture again waits for a click to move down or a double-click to move up and no longer exposes physical-wheel capture. Both packages remain .NET self-contained and now include the x64 Visual C++ runtime required by recording on clean Windows installations.

Version 2.7.1 fixes the embedded C++/CLI recording component failing to load from the 2.7.0 single-file packages on some systems, which prevented region recording from starting. The component now ships as a side-by-side file with SnapCut and requires no separate user installation; packaging fails early if it is absent. Recording startup failures also report the concrete exception type when Windows provides no message.

Version 2.7.0 adds configurable region video recording with H.264/H.265, 24/30/60 FPS, system audio, microphone, keyboard/mouse input overlays, and dedicated hotkey, tray, and floating-menu entry points. Recording controls stay unobtrusive, completion is reported, and screenshots remain available during a recording. The new configurable floating button remembers its multi-monitor position, docks and partially hides at screen edges, and can repeat the last region or launch capture, recording, scrolling capture, pinning, and all-display capture. All-display capture preserves the Windows monitor arrangement in one composed image. Mouse shortcuts gain modifier/side-button holds and direct-drag selection while preserving normal `Ctrl+click` multi-selection and preventing first-capture click-through. Window placement, tray menus, startup registration, and display-change recovery are hardened. A source-level macOS menu-bar preview adds region and scrolling capture, hotkeys, preview, history, and permission guidance; no unsigned macOS binary is published yet.

Version 2.6.1 fixes release history becoming empty when both Gitee and GitHub public APIs are rate-limited. History now prefers a static manifest that consumes no API quota, then falls back to the bundled manifest or the last successful local cache when the network is unavailable. A temporary refresh failure also preserves the list already on screen. Both packages include the formal release manifest, while verified update and rollback downloads retain mirror fallback and SHA-256 validation.

Version 2.6.0 adds a Donate page to Settings with a clearly identified WeChat donation QR code for supporting continued maintenance, feature development, and the planned macOS version. This release also formally includes the personal non-commercial license notice, and packages the license with both installed and portable distributions.

Version 2.5.1 resolves the conflict between `Ctrl+Left` and file multi-selection in Explorer. Left, middle, and right buttons, including modified combinations, now use the configured hold duration; short clicks and pointer movement continue to reach the active application, while holding long enough still transitions directly into drag-to-select capture. Startup registration now validates the complete launch command, and an online update refreshes an enabled startup entry from an obsolete executable path to the current installation path.

Version 2.5.0 adds complete mouse hotkeys: use Back or Forward directly, or combine `Ctrl`, `Alt`, and `Shift` with left, middle, right, Back, or Forward. Unmodified left, middle, and right buttons use a configurable `300–2000 ms` hold; side buttons can trigger immediately or use the same hold duration. When left or right starts region capture, translation, pinning, or scrolling capture, keep holding and drag immediately, then release once to finish the selection. New mouse hotkeys are paused while a capture session is active, and every finish, cancel, close, or error path releases capture ownership to prevent click-through, duplicate activation, or a stuck mouse button. Mouse input is also intercepted while recording a shortcut so other applications are not triggered accidentally.

Version 2.4.0 upgrades the former OCR hotkey into one-step translation: after region selection SnapCut performs local OCR, translates through the configured provider priority, and shows the translated overlay while preserving existing hotkey settings. Online configuration adds a custom endpoint and 14 built-in vendors including OpenAI, DeepSeek, Qwen, Claude, Gemini, and Grok; vendor endpoints are filled automatically and API keys remain separate per vendor. Provider rows now show live available/unavailable badges with precise hover reasons for missing credentials, model-list failures, invalid models, or missing offline files. Returning Settings to the foreground revalidates online and offline providers. Settings inputs no longer steal page-wheel scrolling, and refreshing a model list no longer clears the selected model.

Version 2.3.1 makes online and offline translation providers user-sortable and automatically tries the next provider when the current one fails. Large offline-model downloads now detect connection or read stalls, retry automatically, and retain language directions that already passed verification. Cleanup is hardened for locked files, access-denied errors, and read-only directories; a noncritical stale-directory cleanup failure no longer reports a completed model installation as failed. Installed and portable updates preserve `TranslationModels`, and technical paths or filenames are no longer sent for translation.

Version 2.3.0 adds a choice between online LLM translation and local Bergamot models, automatically detecting the source language and translating to the selected target language. Before an offline package is installed, Settings shows the download size, installed footprint, available disk space, and destination. The Update page now lists formal release history, publication times, release notes, and update/rollback actions for verified builds, while checking again whenever Settings returns to the foreground. Hotkey recording captures key combinations inside SnapCut so entering combinations such as `Alt+A` does not invoke another application's global action, and portable-build persistence for hotkeys and themes is fixed. Light/dark styling and window placement persistence are completed across regular captures, scrolling-capture editors, and text result windows. Scrolling capture also receives further fixes around its initial viewport, reverse extension, and repeated visual patterns. Starting with this release, packages use the `SnapCut-` filename prefix, with both the primary and legacy manifests pointing to the same assets.

Version 2.2.2 fixes the new-version indicator in the Settings sidebar. When an update is available, the existing “Update” label is replaced in place by a highlighted “New version” label with a tooltip showing the target version; when no update is available, the original text and navigation color return. The separate badge that could overflow the narrow sidebar and show only one character has been removed, with regression coverage for highlight, reset, and sidebar bounds.

Version 2.2.1 prevents scrolling capture from accepting implausibly large motion after pausing and resuming, which could insert repeated content. Reverse extension after returning to the initial viewport now requires stricter, consecutively consistent image evidence, preventing periodic code lines from being inserted at the wrong position, compressed, or omitted. Diagnostics also include overlap rows, confidence, horizontal offset, and expected motion for more precise investigation of edge cases.

Version 2.2.0 makes existing rectangles, arrows, text, and emoji directly selectable for later adjustment of position, size, endpoints, and corners, with distinct pointer feedback for selectable, placing, and editing states. Rectangles respond only on their precise border so their interior does not block placing another annotation. Settings now checks for updates every time it returns to the foreground and uses refreshed soft light gradients and deep cyan gradients in dark mode. Capture-toolbar actions use consistently sized outline vector icons while retaining the existing emoji icon. Scrolling capture receives additional fixes for the initial-viewport seam, pause settling, reverse extension after returning, and complete code lines lost or compressed on periodic content.

Version 2.1.0 rebuilds scrolling capture around fixed-cadence controlled wheel input: click to start, pause, or resume; double-click to return to the initial viewport and extend in the opposite direction; and move the pointer freely throughout. Stitch anchors now use the input position at the exact capture instant, fixing missing rows, ghost seams, false boundaries, and permanent alignment stalls after pauses or return trips on repetitive code. The tall-image viewer also gains fit-to-window sizing, `Ctrl + mouse wheel` zoom, and direct panning without a visible resize handle. This release includes the new cross-platform stitching core and a macOS command-line prototype while preserving Windows behavior and data layout.

Version 2.0.0 renames the application to SnapCut with a new icon, rewrites the scrolling-capture stitching core (sequential chain matching, sampling backpressure, DwmFlush-aligned capture, and boundary re-anchoring) so any speed and direction changes stitch reliably, shows the whole stitched image in a growing live preview, reduces idle memory to about 25 MB, upgrades stickers to 88 native color emoji (including the scrolling-capture editor), introduces WeChat-style tapered arrows, and adds an application-level exception net that logs to `ScreenshotData\Diagnostics\crash.log` instead of crashing. **Portable users upgrading from 1.3.2 or earlier**: because the executable was renamed from Screenshot.exe to SnapCut.exe, the old in-app updater cannot apply this release automatically - download the new portable package once manually (copy the old `ScreenshotData` folder over to keep your data). Installed builds still update in place automatically.

Version 1.3.0 introduced in-app updates. Starting with 1.3.1, the dedicated Update page probes the Gitee China mirror and GitHub concurrently, uses the first valid manifest, and switches source automatically if a download fails. Every package is checked against its declared size and SHA-256. Installed builds update in place; portable builds safely replace program files; both preserve `ScreenshotData` and restart automatically. Users on 1.2.0 or earlier need one final manual upgrade to 1.3.1, after which future releases can be installed in the app.

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
| Translation | `Ctrl+Alt+O` |
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

Installer and portable assets use the `SnapCut-` prefix. The build script also creates the primary `SnapCut-Update.json` manifest and the legacy `Screenshot-Update.json` endpoint. All four files are written to `installer/dist/`, and both manifests are copied to `updates/` for stable Gitee raw URLs. Upload all four files to the matching GitHub and Gitee releases; both manifests reference the same `SnapCut-` packages, so large binaries are not duplicated. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the architecture and [PLAN.md](PLAN.md) for implementation status.

### macOS preview

The macOS frontend is isolated from the Windows WPF application and reuses the platform-neutral scrolling-capture core. Running `snapcut` without arguments starts the menu bar app. Defaults are `⌘⇧A` for region capture and `⌘⇧S` for scrolling capture; Settings manages hotkeys, screen-recording/input-monitoring permissions, and recent captures.

```powershell
pwsh ./build-macos.ps1 -Runtime osx-arm64 -Version 0.1.0
# Use -Runtime osx-x64 for Intel Macs.
```

See [SnapCut.Mac](src/SnapCut.Mac/README.md) for permissions, signing, packaging, and real-Mac validation requirements. No signed or Apple-notarized macOS binary is published yet.

