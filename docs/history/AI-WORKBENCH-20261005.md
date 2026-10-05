# AI 双模式扩展记录 · 2026-10-05

## 实现

- 普通设置移除 AI 分组；AI 页右上角改“设置”，新增独立页面和配置文件，旧 Key 与模型偏好兼容。
- 既有模式命名“基础模式”；新增独立“工作台”工具目录、提示词、官方模板初始化、源码/资源文本编辑、受限终端、依赖源码查询和真实 JAR 校验。
- 独立基础子代理处理启动器业务准备，不能接触项目编辑/终端；任务组和历史隔离、保留人类确认与取消。
- 可收起会话侧栏，左上角模式选择；分模式加密持久化、检查点保存、恢复只读执行记录、重新授权项目，不重放旧操作。
- 九篇原创整理的内置开发指南覆盖 Fabric/Forge/NeoForge、环境、资源、网络、测试、排错与交付，附官方来源和审校日期。
- 增加显式 JDK 下载选项，默认游戏 Java 下载不变；构建重复执行不复用上次结果，授权期间构建配置变化拒绝执行，长终端输出保留头尾。

## 已有证据

- Release 解决方案构建通过，无错误；有五个既有 xUnit 分析器警告。
- 最终完整回归：170 通过，0 失败，2 个门控联网测试明确跳过，共 172，约 37 秒。父子代理共享预算和保存/删除竞态防护已纳入最终版本；Agent 专项 38 项也已通过。证据 `artifacts/agent-workbench-check/workbench-full-regression.trx`。
- WPF 离屏渲染/事件检查覆盖深浅主题、独立设置、模式与侧栏切换、会话保存/恢复、现有问答/滚动/输入控件及 46 DIP 顶栏。截图在 `artifacts/ui`，文档媒体复制到 `docs/images`。
- 实际 NeoForge 1.21.1 官方 MDK，提交 `7819b902a351b03fe71db00754d103b5a31c4ebf`，Java 21 / Gradle 9.2.1：通过工具查询、初始化、实际 build，找到正式 Mod JAR。约 4 分 39 秒，第一次下载依赖。证据 `artifacts/agent-workbench-check/workbench-live-build.trx` 与验收项目的 `verification.json`。
- 使用已有 DPAPI Key 的真实 DeepSeek 流程：读取/检查独立验收项目、新增一个 Java 类、运行 Gradle build、确认该 class 进入 JAR；随后真实派发基础子代理查询状态。约 39 秒通过。证据 `workbench-live-deepseek.trx` 和 `deepseek-verification.json`。子代理业务状态来自测试夹具，不使用真实账户或游戏，不声称已验收真实账号切换/安装。
- 最新 Full/Lite 单文件 EXE 均以 `--verify-package` 离线执行通过（退出码 0）：四个普通页面、独立 AI 设置页、九份内嵌指南、分模式加密会话、Steve 头像与 15 份内嵌许可。证据 `artifacts/agent-workbench-check/package-full.json` 与 `package-lite.json`。产物位于 bin，源码 ZIP/哈希清单同步更新。
- 发布脚本处理正在运行的旧 Lite 文件时，补齐临时旧文件删除的 UnauthorizedAccessException 分支；仅暂存被锁定的旧 EXE，新包已写入，不强行关闭正在使用的启动器或游戏。运行中的旧版本需由用户重启后切换。

## 验证边界

本次真实 AI 测试目标是小型 Java 功能的生成、编译、打包与委托协议，不等于所有玩家需求、加载器/版本组合或多轮大型 Mod 均能成功。真实 Fabric/Forge 模板构建未扩大实测矩阵。

本次未执行游戏内功能/渲染/多人/专用服务端验证，也未将离屏 WPF 检查写成人工桌面/IME 验收。默认不自动运行 Minecraft。Windows Job 与权限检查约束工具入口和生命周期，Gradle 脚本执行不是操作系统沙箱。

完整对话、推理、密钥及真实用户目录未写入上述验收记录，未纳入源码包。项目源码、缓存和输出留在被忽略的 artifacts 下。
