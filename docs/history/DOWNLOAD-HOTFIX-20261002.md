# 下载模块快速修复 · 2026-10-02

- 游戏列表仅显示自行命名的实例。新安装标记基础版本和加载器为内部依赖，旧安装通过既有安装记录识别；独立实例及带用户配置的目录保留展示。父版本 JSON / JAR 保留在磁盘供 CmlLib 解析，不删除启动所需文件。
- 模组改为三步：发现 → 全部文件版本与目标 → 确认安装。文件列表不传目标版本或加载器筛选，切换目标不重取或清空文件选择。版本项显示加载器及 Minecraft 版本，CurseForge 分页读取可访问文件。
- 模组不执行 Minecraft / 加载器 / Java / 本地 Mod 兼容性检查，不自动下载依赖。只安装所选版本；下载大小 / 哈希、取消、暂存清理及同名不同内容拒绝覆盖仍保留。确认页仅保留“兼容性由你确认”。
- 修正 Modrinth `environment` 的字符串 / 对象响应兼容，去除文件列表额外的项目请求。
- 卡片在首次布局前即隐藏并放在可视区域外，延迟期间维持入口位置，再依次滑入；调整缓动曲线，完成后释放动画时钟。快速返回、缓存重入和关闭动画均重置状态。

验证：本轮仅运行相关的 **20 项检查，全部通过**，约 4 秒；未重复全量矩阵。检查覆盖旧安装父版本隐藏、完整继承保留、不同版本 / 加载器模组返回、手动安装跳过兼容性及依赖、文件校验、三步页面、动画延迟首帧与关闭动画等。

实际请求 MCIM 和 Modrinth 官方的 Sodium 文件列表均返回 **256 项**；将所选 Minecraft 1.21 文件安装到独立的 1.7.10 目标目录，下载成功且哈希通过，仅写入选定 JAR，没有额外依赖、Java 下载或兼容性拦截。这验证手动下载行为，不代表该跨版本组合可以运行。旧安装目录扫描只返回命名实例，不再显示原版和加载器父版本。证据在 `artifacts/oct02-hotfix/result.json`。

动画首帧使用离屏原生窗口检查通过；人工桌面手感未标记为实测。最终 Release 编译和 Windows x64 自包含发布通过，保留已有账户、设置和游戏数据。

发布包：`bin/iKunLauncherNext-win-x64-20261002-hotfix.zip`。源码：`bin/iKunLauncherNext-source-20261002-hotfix.zip`。解压发布包后运行 `iKunLauncherNext.exe`，更新时保留原 `data`、数据路径引导文件、`runtime`、`.minecraft`。

API 行为参考：[Modrinth 全版本接口](https://docs.modrinth.com/api/operations/getprojectversions/)、[CurseForge 文件分页接口](https://docs.curseforge.com/rest-api/#get-mod-files)。
