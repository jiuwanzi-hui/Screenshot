# Windows 翻译右键与临时便携包验证

2026-09-20，当前版本 4.1.7，仅修正 Windows；无版本递增、提交、推送或远程发布。

## 修复

- 截图层右键预览事件排除“译”按钮及其文本、图标节点，继续由 WPF 打开语言菜单，不预先撤销或登记松手关闭。截图背景右键保留原有行为。
- 删除全局翻译语言菜单的 F4 绑定和截图悬停提示中的 F4；保留右键、聚焦后 Alt+下键、单击直接翻译和语言全局同步。
- 打包必需文件清单补入已有 Qwen3-VL-4B 许可证。没有放宽文件清单，也未省略依赖。

## 验证

- 工具栏、翻译入口及准备路径：46 项 Release 回归通过。之后补强并重新通过 3 项右键编辑状态测试，覆盖按钮／TextBlock／Run，确认无标注不退出、有标注不撤销、背景右键仍撤销。
- `installer/build-installer.ps1 -Version 4.1.7 -LocalTest`：更新相关 61 项通过，1 项真实 Gitee 联网下载测试按默认开关跳过；不执行真实更新安装。
- Release 编译成功、0 错误；3 项既有分析器警告位于 VisualOcrService、SettingsViewModel。
- 7z 完整性检查通过；ZIP 的 13 个文件逐项 SHA-256 与发布暂存一致。包含 .NET、自有程序、VC++ 录屏运行库、离线推理运行库和许可证，无用户配置、密钥、模型、调试符号或诊断采集脚本。
- 最终 `installer/dist` 再次通过 `verify-update-artifacts.ps1 -LocalTest`：版本、测试渠道、历史清单、双源 URL、大小及 SHA-256 均匹配。地址为保留地址，本次没有上传。
- 最新源码 Release 已启动，核实 PID 47824 的路径为 `src/Screenshot.App/bin/Release/net8.0-windows10.0.19041.0/SnapCut.exe`。
- 失败及成功两份发布暂存、空的打包暂存目录均已清理；首次强制清理被自动审批拒绝，随后使用不强制删除的方式清理成功。保留小体积日志在 `artifacts/windows-translation-rightclick-20260920`。
- 未在其他电脑实测硬件、显示驱动和首次工具栏耗时；右键回归使用隐藏 HWND 的 WPF 路由，无全局鼠标注入。

## 交付

便携包位于 `installer/dist`，沿用同名包覆盖最新 Windows 测试产物，Mac 文件保留。程序产品版本为 `4.1.7-localtest`。

| 文件 | 字节 | SHA-256 |
| --- | ---: | --- |
| SnapCut-Portable-4.1.7-win-x64.7z | 97019343 | 575D69D0F1221816F12B4E7A7667BF2ED418F9F37BD7D5935BEF4FB313580E43 |
| SnapCut-Portable-4.1.7-win-x64.zip | 130939571 | AB00063FC581FA9207C0B3DE93D5B4DA278099E86A188EE4B3B070DD79A355D6 |

同步生成本地测试安装 EXE、测试更新元数据、SHA256SUMS 和临时验证说明。可选模型仍按设置下载。
