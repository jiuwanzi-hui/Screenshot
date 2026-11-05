# Windows 离线翻译模型下载与导入

在设置 → 翻译 → TranslateGemma 4B 中点击“下载多语言模型”。模型优先使用魔搭国内源，随后尝试备用源。连接超过 8 秒、读取连续 15 秒无数据时自动换源；已下载数据保存在 `.part` 文件中，下次继续。文件大小、续传位置与 SHA-256 均通过校验后才安装。

Windows 安装包和便携包附带约 34 MB 的 llama.cpp 推理组件，客户无需另外连接 GitHub 下载该组件。该压缩包只占磁盘空间，不在启动时解压或加载模型。

## 内存较紧张的电脑

设置 → 翻译 → 本机离线翻译引擎中可选择 **Hy-MT2 1.8B · 轻量多语言模型**，再下载或导入对应 GGUF。该模型约 1.1 GB，使用 CPU 推理；与 TranslateGemma 4B 分目录保存，不替换原模型，也不自动改变原来的选择和在线 / 离线优先顺序。模型文件大小不等于运行内存占用，首次加载和翻译耗时仍取决于文本与硬件。

官方来源：[腾讯模型说明](https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF)。模型按 Apache 2.0 许可提供，完整许可随 Windows 包的 `Licenses/Hy-MT2-1.8B.txt` 提供。

| 文件 | 字节数 | 国内下载源 |
| --- | ---: | --- |
| `Hy-MT2-1.8B-Q4_K_M.gguf` | 1,133,080,448 | [腾讯魔搭仓库](https://modelscope.cn/models/Tencent-Hunyuan/Hy-MT2-1.8B-GGUF/resolve/ef1d40b8b315575d30d1eb6579996130b8fc0bb2/Hy-MT2-1.8B-Q4_K_M.gguf) |

SHA-256：`dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699`。

在线模型仍严格按配置顺序使用；失败后切换离线时，结果提示会保留在线失败的原因。配置中的“测试模型”实际翻译两段文字并校验结果，不能只凭接口返回成功就认定翻译可用。

## 无法联网时

在能够下载的电脑上取得下面的文件，拷贝到客户电脑，点击“导入本地文件”。支持多选；新版 Windows 包通常只需导入 GGUF。导入过程不联网，错误文件不会替换已有模型。

| 文件 | 字节数 | 来源 |
| --- | ---: | --- |
| `translategemma-4b-it-Q4_K_M.gguf` | 2,489,909,312 | [魔搭主源](https://modelscope.cn/models/bullerwins/translategemma-4b-it-GGUF/resolve/5f0545305035039384fde236d8be8237b1c46102/translategemma-4b-it-Q4_K_M.gguf)、[魔搭备用源](https://modelscope.cn/models/42ailab/TranslateGemma-4B-GGUF/resolve/fd2e388a08a8f11159dcdaf9e637975517b288c3/translategemma-4b-it-Q4_K_M.gguf) |
| `llama-b10276-bin-win-vulkan-x64.zip` | 34,096,096 | 新版程序的 `OfflinePackages` 文件夹，或 [llama.cpp 官方文件](https://github.com/ggml-org/llama.cpp/releases/download/b10276/llama-b10276-bin-win-vulkan-x64.zip) |

保留原始文件名；不需要手动解压 ZIP。仅导入其中一份时，程序说明还缺少的组件，可以继续导入或下载补齐。共享的多语言模型不需要按语言重复下载。

SHA-256：

```text
translategemma-4b-it-Q4_K_M.gguf
7f7357c14abd9da4eb200b38b05da502cd6e10d7e1d403fbc9f78c19f3209b72

llama-b10276-bin-win-vulkan-x64.zip
e9f0f8cd772258a1bbc3c1f39bedbb63b2ce176ddfd1d2eecdd2be65406eba70
```

公共下载源仍受运营商、代理、服务可用性影响。商业交付可以预装经校验的模型，或者提供本地离线文件；本功能没有使用第三方 GitHub 公共加速代理分发推理组件。

## Windows 构建

`installer/build-installer.ps1` 会先准备并校验推理组件，再检查发布目录包含相同文件。普通源码构建如需附带组件，先执行：

```powershell
./installer/prepare-translation-runtime.ps1
# 构建机离线时可以指定预先下载的原始文件：
./installer/prepare-translation-runtime.ps1 -ArchivePath 'D:\models\llama-b10276-bin-win-vulkan-x64.zip'
```

构建缓存位于已忽略的 `artifacts/translation-runtime`，不把大二进制文件提交到 Git。组件作为 `OfflinePackages` 内容文件复制到 Windows 输出，单文件发布时也保持在程序旁；不会内嵌进程序集或在启动时加载到内存。
