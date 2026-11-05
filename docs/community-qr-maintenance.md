# 更新交流群二维码

程序在打开“交流联系”页面时先显示已有图片，再请求 GitHub `main` 分支的远程图片；远程图片加载成功后替换显示，网络失败时保留已有图片。退出该页面再进入会重新请求，不必重新发布安装包。

1. 将新的图片保存为真正的 JPEG 格式，文件名使用 `SnapCutCommunityQr.jpg`。
2. 在内置浏览器打开 https://github.com/jiuwanzi-hui/Screenshot/tree/main/src/Screenshot.App/Assets ，确认分支是 `main`。
3. 点 **Add file → Upload files**，选择这个同名文件；不必先删除旧图。
4. 确认变更仅包含 `src/Screenshot.App/Assets/SnapCutCommunityQr.jpg`，填写 `更新交流群二维码`，选择直接提交到 `main`，点 **Commit changes**。
5. 打开下面的远程图片链接核对内容，然后重新进入软件的“交流联系”页面。GitHub 缓存或网络限制可能导致旧版程序稍后才看到变化。

远程图片地址：
https://raw.githubusercontent.com/jiuwanzi-hui/Screenshot/main/src/Screenshot.App/Assets/SnapCutCommunityQr.jpg

这次网页提交只更新公开图片资产，不包含本地后续源码。不要上传整个本地项目目录。

GitHub 操作说明：https://docs.github.com/en/repositories/working-with-files/managing-files/adding-a-file-to-a-repository
