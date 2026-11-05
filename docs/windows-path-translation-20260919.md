# 含路径正文被误判为目标语言（Windows，2026-09-19）

用户原图中的英文安装错误包含 `C:\Program Files\Paradox Interactive`。当前本机配置目标语言为 `zh-Hans`。问题发生在调用模型之前：`TranslationTargetLanguageMatcher.IsLikelyInvariant` 原来只要在整段里发现反斜杠，就把整段标为无需翻译；`IsAlreadyTargetLanguage` 复用了这个结果，截图工具栏又显示“已经是目标语言”。模型没有机会翻译这段正文。

## 修复范围

- 共用判定层只保护独立资源引用；正文里出现路径、网址、邮箱不再使整段跳过。带空格的路径按根路径和各目录组件判断；存在句子特征时保守进入翻译。独立路径、网址、邮箱、文件列表的数字标记仍保留。
- `Uri.TryCreate` 也会接受类似 `Status: installation failed` 的文本，不再用其成功与否判断整段免译。去掉“首个词像文件路径则整段免译”和“以 `>` 结尾则免译”的过宽规则。
- 截图提示区分“目标语言或无需翻译的内容”，不再把所有被跳过的内容都说成目标语言。
- 保持用户的目标语言、模型、提供方顺序、截图像素及既有资源释放逻辑。仅改 Windows；不改 Mac、长截图算法或版本号。

## 验证证据

1. 修复前新增回归 16 项失败，14 项通过，1 项需实际模型的测试默认跳过。完整英文段落和含换行版本都能复现；实际提供方的请求列表为空。
2. 最终相关回归 246 项通过，11 项依赖模型的测试默认跳过。覆盖原图文本、OCR 分行、说明前后出现路径、UNC、短提示、网址、邮箱、法语正文、独立技术引用，以及既有中英混合、日语上下文、复合标签、提供方顺序、结果完整性和失败索引。
3. 随后单独启用真实模型测试，使用用户原图中的选区，执行与截图工具栏相同的 OCR、文字框调整、段落合并、翻译和覆盖文字规范化。真实 OCR → TranslateGemma 4B → 可覆盖译文测试通过；未调用在线服务。

OCR 实际读出：

> The installer has insufficient privileges to access this
> directory: C:\Program Files\Paradox Interactive. The
> installation cannot continue. Log on as administrator or
> contact your system administrator.

实际译文：

> 安装程序没有足够的权限访问此目录：C:\Program Files\Paradox Interactive。安装过程无法继续。请以管理员身份登录，或联系您的系统管理员。

本机 RTX 5070 Ti 上 OCR 为 622 ms；离线模型首次请求包含加载共 10,172 ms，同内容再次请求为 256 ms，后者可能复用缓存，不能当作新文本推理速度。此轮修复的是错误跳过翻译，不宣称消除了模型冷加载时间，也不把本机耗时推广为 4800H 耗时。

小体积原始结果位于 `artifacts/translation-path-20260919`：`baseline.trx`、`regression-final.trx`、`ocr-offline.trx` 及对应日志。最新附件及打包校验见 [4.1.7 本地交付记录](releases/4.1.7-localtest-validation.md)。前一轮 4800H 冷工具栏和焦点修复继续保留，目标机复测结论仍以新增诊断日志为准。
