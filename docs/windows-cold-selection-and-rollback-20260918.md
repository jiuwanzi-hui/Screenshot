# Windows 冷框选与版本回退修复（2026-09-18）

## 复现线索与原因

- 用户确认回退场景为 **4.1.2 免安装版 → 4.1.0**，安装版未测试。
- 普通截图的 resident 原生框选仍在处理鼠标时，feature worker 已安装 UI 线程上的全局输入钩子，然后同步准备 WPF 工具栏。该准备路径只用于普通截图，会使低级输入回调等待冷加载；钉图、录屏没有相同工具栏准备路径。
- 4.1.2 的配置包含 4.1.0 未定义的 `CaptureToolbarFeature.AspectRatio`。旧 `JsonStringEnumConverter` 抛出异常，旧 `SettingsStore` 因而整份返回默认设置。新增字段本身可以被忽略，新增枚举名称不能。
- 对比本地 `v4.1.0` / `v4.1.2`：AppMetadata、三种模型管理器、PortableUpdateRunner、PortableUpdateArchive 和 SettingsStore 没有代码差异。便携包解压原本已排除 ScreenshotData 和 TranslationModels；尚无证据证明本次回退实际删除了所有模型文件。配置回落会改变已选的 OCR / 翻译引擎。

## 改动

- resident 框选期间暂不在 feature worker 安装全局输入钩子；收到最终选区或取消命令再接管。保留工具栏提前准备及按需进程退出。
- 更新和回退都使用当前程序的更新器，不再执行目标旧包里的更新器。单文件发布包与源码 Release 的框架依赖构建都可创建独立 helper；helper 名称避开安装器的 SnapCut.exe 关闭过滤器。
- helper 在程序入口直接处理更新，避免启动 WPF 和从 helper 目录读取用户配置。
- 真正降级时，先将原始配置字节备份到 `ScreenshotData/VersionBackups`，再将已知枚举转换为旧转换器支持的数字表示（包括数组和字典键）。保留未知字段及普通文本。相同版本重装和升级不改写配置。
- 兼容配置的写入发生在替换程序文件之前。备份、解析或写入失败时停止；兼容配置也可被当前版本正常读取。
- 在文件提交阶段再次排除 ScreenshotData / TranslationModels。安装脚本也显式排除两个目录；取消卸载时无条件删除模型的规则，改为只有用户选择删除用户数据才一并删除模型。

## 验证

- Windows Release 编译成功；现有 SettingsViewModel 的 CA1859 警告仍在。
- 相关回归 **164 通过、6 跳过、0 失败**，记录：`artifacts/verification/memory-rollback/final-regression.trx`。
- 最后补充配置根节点验证后，8 项回退回归再次通过：`rollback-final.trx`。
- 旧版枚举读取器复现新配置读取失败，兼容转换后保留快捷键、保存目录、主题和已知工具栏设置；完整原始配置备份逐字节一致。
- 覆盖 ZIP/7z、设置/凭据/历史/水印/缓存/模型保留、文件锁导致写入失败后的原文件恢复、异常配置停止回退、helper 实际独立启动和取消框选后的实际 worker 退出。
- 6 项跳过项包含依赖真实桌面输入/窗口的集成测试及显式联网 Gitee 下载测试。本次未执行安装器在真实安装目录的降级，未用用户数据执行实际降级；未声明已验证所有硬件的拖动延迟或已恢复用户此前丢失的数据。
- 只读检查发现本机便携目录和源码 Release 目录内的 settings.json、加密凭据及多个 OCR/翻译模型目录仍在；目录存在不能代替全文件完整性校验。

修复阶段仅修改 Windows，未修改 macOS 或长截图算法。后续 4.1.5 打包与发布验证见 [4.1.5 发布验证](releases/4.1.5-validation.md)。
