# 双模式 Agent、独立配置与持久会话

实现基线：2026-10-05，.NET 9 / WPF，既有 DeepSeek Responses 适配器。保留基础模式业务入口和默认权限，工作台另建工具目录与系统提示词。

## 服务边界

- `AgentSessionService`：复用 ReAct、完整响应校验、工具/输出配对、问题、确认、预算、取消、压缩与上下文统计；可注入模式专用提示词。检查点只在用户消息、完整响应无调用、整批调用结束、压缩成功等稳定边界产生。
- `AgentToolRegistry`：基础目录。新增兼容的 `download_java.package_type` 可选参数，默认 JRE，开发使用 JDK 并校验 `javac.exe`。基础模式的其他工作流保持不变。
- `WorkbenchToolRegistry` / `WorkbenchOperations`：项目工具和官方模板。工作台不会直接获得基础安装/内容/删除工具。
- `BasicAgentDelegate`：创建独立基础 ReAct 会话与任务组；共享模型连接及人类交互入口，隔离历史、提示词、工具目录和项目授权。等待真实结果，取消沿用父轮 token。子代理不能递归派发。
- `AgentModelBudget`：父轮、基础子代理及压缩共用最多 24 次模型请求和 64K 输出确认阈值，委托不会倍增调用额度。预算耗尽保留已完成结果，不冒充目标完成。
- `AgentConversationStore`：两种模式分别保存 DPAPI 加密、原子替换的快照。协议历史不写日志。恢复不执行历史调用；未完整配对的历史调用补失败输出，重新查询状态和 ID。
- `AiConfigurationStore`：`data/ai/settings.json` 只保存模型/推理/首次配置/侧栏/模式；一次性读取旧字段作为迁移默认值。密钥仍由已有 `SecretStore` 保存。
- `AgentManagement` / `AiSettingsPage`：会话侧栏、模式选择、独立设置、项目选择器、授权与恢复；普通 Settings 不显示 AI 项。

## 工作台工具清单

| 分类 | 工具 | 参数/权限 |
| --- | --- | --- |
| 指南 | `get_development_guide` | 九个固定专题，参考资料不授予权限 |
| 状态 | `inspect_workspace` | 实际授权状态、文件和 JDK 逻辑 ID |
| 官方模板 | `list_mod_templates`, `create_mod_project` | 实时查询→保存模板 ID→空目录初始化；官方固定端点，无模型自选 URL |
| 源码 | `list_workspace_files`, `read_project_file`, `search_project` | 相对路径、受限文本、SHA256；排除缓存、链接、运行数据和敏感文件 |
| API 核实 | `list_dependency_sources`, `read_dependency_source` | 仅该项目缓存中实际下载的 sources.jar；查询逻辑 ID 后读一个 Java 条目，限长，不执行 JAR |
| 编辑 | `write_project_file`, `delete_project_file` | SHA256 乐观并发校验；新文件空 hash；覆盖/删除先确认、确认后重检 |
| 开发终端 | `run_terminal` | Gradle Wrapper 的 build/check/test/classes/compileJava/tasks/runData/genSources；Java version；Git status/diff；无任意命令 |
| 产物 | `list_build_artifacts` | `build/libs` JAR、大小、hash、Mod 元数据，过滤源码/开发/文档包 |
| 委托 | `delegate_basic_agent` | 明确的启动器准备子目标；不代替编码、项目创建、编译或调试 |

直接保留的基础工具只有：`get_launcher_state`, `select_game`, `select_account`, `configure_java`, `retry_account_login`, `read_game_logs`, `launch_game`, `ask_user_question`。工作台及其委托中的启动需要人类确认；默认目标是编译与打包。

Schema 拒绝未知字段并限制长度，不能猜测查询返回的 ID。构建命令不复用普通写操作去重缓存，否则“修改源码后再次 build”会错误跳过编译；其结果由每一次实际进程产生。

## 项目与进程权限

人类原生选择器授权独立项目，模型不能设置绝对根路径；每个路径验证所有现有祖先，拒绝越界和 reparse point。创建模板检查 ZIP 绝对/上级/ADS/链接路径及大小；失败只清理本次创建的文件和空目录。已有文本修改匹配原 hash，确认绑定对象与新内容，再复核竞态。

Gradle 通过识别到的 JDK 运行项目 Wrapper JAR，不执行 `gradlew.bat`，首次/配置指纹变化需信任确认，确认期间变化拒绝运行。指纹覆盖受支持的构建文本、buildSrc/build-logic、properties、version catalog 与 Wrapper。受限参数不是操作系统沙箱：Gradle、插件、注解处理器和测试仍可执行代码、访问系统/网络，不能宣称阻止可信构建里的所有恶意行为。

环境只传必要系统、JDK、项目缓存变量，不传 API Key/令牌；输出脱敏、限长，保留头尾供排错。Windows Job Object 管理本次进程树，关闭、取消或 15 分钟超时终止本次构建；不终止其他任务/游戏。Git 只读操作禁用外部 diff、textconv、hooks 与 fsmonitor。

已有覆盖和删除按当前约定逐项确认；模型不能授权。内嵌文档、注释、日志及网页仍是不可信输入。没有自动源码上传或发布工具。

## 工作流与版本策略

先需求澄清和目录授权→官方模板/版本/映射/JDK→编译基线→小步编辑→按真实错误修复→最终 build/check 与 JAR 核实。工作流拆为九份嵌入式 Markdown，避免每次把全部文档塞入提示词。文档版本、来源、审校日期明确；API 不确定时从当前依赖源码查询。

Fabric 查询官方例子对应游戏分支并固定提交；NeoForge 查询对应 MDK 仓库提交；Forge 查询官方 promoted MDK。JDK 与 Gradle 组合依据实际项目，不能把新版文档 API 自动套到旧版，不能混用 Yarn 和 Mojang 映射。编译通过不能被表述为运行兼容性验收。

## 会话生命周期

各模式独立目录，记录消息、问题答案、执行树、协议上下文、目标、引用、项目位置和压缩次数。保存通过 UI 快照和后台加密文件操作完成；侧栏虚拟化、像素滚动。恢复后的卡片只读、折叠，不恢复批准、待答任务或目录权限。

切换模式/会话、新对话、退出和关闭先停止执行并保存；停止父轮传递到子代理，开发终端使用同一 token。历史语义不能替代现在的真实目录、文件、账户和逻辑 ID。两种模式仍支持既有上下文观测、手动/自动压缩和会话目标。

## 扩展验收

覆盖目录/Schema 隔离、路径/链接、hash 与授权竞态、构建重复执行、依赖源码逻辑 ID、JDK 类型、基础子代理分组/确认、加密存储与损坏恢复、历史不重放、检查点完整配对、UI 模式/设置/会话恢复。真实模板构建和 DeepSeek 流程用显式门控集成测试，未执行时记录 Skip，不冒充通过。具体证据见 docs/history；不扩大游戏运行版本矩阵。

## 2026-10-05：显式授权模式与压缩保留

默认审批策略不变。输入区由宿主 `AgentPermissionPolicy` 控制权限；用户选择授权模式、确认警告之后才添加完整能力，模型没有切换权限工具。会话恢复和切换不恢复授权模式。`PermissionToolRegistry` 使用动态目录并在执行时重新检查宿主策略；终端/file/目录/路径导入导出工具仅授权模式可用。完整终端在当前 Windows 用户身份下执行，输出限长脱敏，停止及超时通过 Windows Job Object 终止本轮进程树，已有修改保留。

项目选择无需再次确认；可由 set_working_directory 同步工作台目录。完整文件工具接受绝对路径、二进制与链接，不执行旧的项目范围限制；可选 SHA256 是并发校验，不是权限审批。Schema 和结果核实仍生效。人类登录流程不变。

压缩仍使用 LLM，改为结构化交接摘要＋独立目标/引用＋最多 48 个操作检查点的标识和状态＋最近两个完整交互。长日志与工具输出只截取证据字符串，保留调用/返回配对；空间不足保留最后完整交互。检查点保存到加密快照，旧会话没有检查点字段仍兼容。压缩失败不替换原历史，UI 时间线不清空。达到上下文阈值后的完整操作边界压缩策略不变。

Markdown 使用 Markdig Pipe Tables，WPF Grid 展示表头、列对齐、单元格内联格式与主题边框；消息渲染在回收行测量前完成。滚动只在短暂滚轮手势期间运行相对位移计时器，停止/隐藏即停，人工滚动不被流式更新的尾部跟随打断。

模板请求仅使用官方允许地址，处理官方跳转、短暂错误重试、404 和超时；NeoForge 的 ModDevGradle 缺失时查询同版本 NeoGradle。Gradle 构建按项目声明选 JDK，转发系统 HTTP/HTTPS 代理与连接超时，返回实际退出码，并分类网络/TLS、JDK/Gradle与源码错误。