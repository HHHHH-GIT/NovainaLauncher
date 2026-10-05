# AI 显示、授权与构建可靠性修复 · 2026-10-05

## 变更

1. **连贯滚动**：Markdown 在虚拟列表测量前完成渲染，避免回收项先被测为零高度；滚轮改为根据当前锚点逐帧相对移动，不再反复设置绝对偏移。查看历史和拖动滚动条会停止自动跟随，流式输出不会打断滚轮手势。保留像素滚动与虚拟化，关闭动画时直接滚动。
2. **Markdown 表格**：启用现有 Markdig 的 PipeTables 扩展；使用 WPF Grid 渲染表头、单元格、分隔线、对齐和换行，支持单元格内加粗、代码与链接，并使用当前主题资源。没有额外内层滚动区。
3. **审批/授权模式**：输入区新增权限入口。默认审批保留现有权限；授权模式需先由用户确认警告，之后允许完整 PowerShell/cmd、目录选择、任意文本/二进制文件读写、复制/移动/删除和直接路径整合包导入导出。无需项目目录授权或逐项批准。模型不能启用此状态，历史记录不会保存授权；新建/切换会话、退出 AI、重启会恢复审批模式。执行仍受当前 Windows 用户权限约束。
4. **Forge/NeoForge**：按项目 `JavaLanguageVersion.of(...)` 匹配 JDK，显式错误 JDK 会在执行前提示，避免直接选中最高主版本。Gradle JVM 接入 Windows 系统代理与连接超时。官方模板读取增加有限重试和状态说明；NeoForge ModDevGradle 模板不存在时查询同版本官方 NeoGradle。构建错误区分网络/TLS、Java/Gradle 不匹配与实际编译错误，保留真实输出。
5. **上下文压缩**：原流程已经使用 LLM，但只留下简短摘要和少量用户消息，容易遗漏模型答复、工具事实与约束。现在要求模型生成结构化交接摘要，并保留宿主生成的关键操作事实、回答和最近两轮完整交互（过大时保留一轮）。长证据保留首尾摘录，必要时重新读取；目标和引用单独保留。检查点随加密会话恢复。完整摘要接收、工具批次原子执行结束、且确实释放空间后才替换上下文；取消或失败保留原文，界面历史消息不删除。

授权模式的终端使用本轮 Windows Job 管理进程树，停止只终止本轮进程。输出有截取与已有脱敏处理；不将密钥、完整提供商响应和测试会话写入验收记录。已经完成的文件操作不会随停止自动回滚。

## 验证证据

- Release 解决方案构建：通过，0 错误，5 个既有 xUnit 分析器警告。
- 完整自动回归：**175 通过、0 失败、4 个门控联网测试跳过，共 179**；证据 `artifacts/ai-reliability/full-regression.trx`。授权启停、严格参数校验、实际 PowerShell/cmd、文本/二进制文件操作、取消、默认权限回归与压缩工具协议配对均有检查。
- WPF 离屏/事件检查：深浅主题、长消息列表、六次平滑滚轮手势的逐帧偏移、问答卡片滚动、Markdown 表格行/列/单元格及对齐、授权警告、切换后撤销与固定顶栏。截图在 `artifacts/ui`，不等于人工桌面/输入法实测。
- 最后补齐基础子代理的授权模式提示词后，权限/工作台专项 **26 项通过**（`permissions-final.trx`）；完整回归的其余功能未变更。
- 最新 Full/Lite 单文件 EXE 的 `--verify-package` 离线运行均通过，退出码 0，覆盖四个普通页面、独立 AI 设置、九份指南、加密会话、头像和 15 份许可。证据 `package-full.json` / `package-lite.json`。源码 ZIP 和哈希清单同步更新。
- 真实官方模板构建：**Forge 1.21.1 和 NeoForge 1.21.1 均通过**，使用 JDK 21；Forge Gradle 8.12.1 约 5 分 53 秒，NeoForge Gradle 9.2.1 约 4 分 49 秒，均确认正式 Mod JAR。证据 `artifacts/ai-reliability/official-builds.trx` 及 `live-forge` / `live-neoforge` 的 `verification.json`。使用独立验收项目，不修改用户 Mod 项目。
- 真实 DeepSeek 压缩：`deepseek-flash`，本地上下文估算约 **106.6K → 10.3K tokens**；较早的 `campfire_soup`、Minecraft 1.21.1、JDK 21 与最近完整消息保留，继续请求以结构化结果正确读出模组 ID、Minecraft/JDK 版本和禁止启动/上传/修改已有配置的约束。证据 `llm-compaction.trx` / `llm-compaction.json`。测试中的重复日志是合成夹具，不是用户日志，未保存完整测试会话。

当前构建排查确认了原自动选 JDK 和 JVM 代理传递的薄弱点，但没有找到此前失败的完整构建输出，因此不把此前用户失败归因于一个已确认原因。实际验收覆盖上述两个版本，不代表所有版本/插件组合。

## 技术依据与边界

- [NeoForge 1.21.1 环境与首次构建说明](https://docs.neoforged.net/docs/1.21.1/gettingstarted/)。
- [Forge 1.20.1 环境说明](https://docs.minecraftforge.net/en/1.20.1/gettingstarted/)；本次真实 Forge 构建是 1.21.1，不能混作 1.20.1 验收。
- [Gradle 网络代理](https://docs.gradle.org/current/userguide/networking.html)、[Wrapper 配置](https://docs.gradle.org/current/userguide/gradle_wrapper.html)。

本次没有启动 Minecraft、使用真实游戏目录进行破坏性操作或做人工桌面长会话验收。LLM 摘要仍可能遗漏细节；重要长期要求可写入 `/goal`，保留的状态需要执行前重新核实。系统代理受代理实际配置与目标网络可达性影响，不承诺所有网络都能下载。