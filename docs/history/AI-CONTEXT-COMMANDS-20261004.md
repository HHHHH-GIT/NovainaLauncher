# AI 上下文可观测性、压缩与输入命令

## 实现

- 在 AI 模型选择旁加入静态矢量用量圆环。悬停打开并在离开圆环时关闭，点击固定后只通过 × 关闭；隐藏页面、登录界面或最小化时关闭浮层。普通顶栏仍为 46 DIP。
- 面板展示使用占比、容量、分类、最近请求的 API 输入/输出 token 数、压缩次数与释放量。分类包含系统提示/工具定义、用户输入、模型输出/推理、工具调用/结果、日志、摘要、目标/引用；分类不重复计数且总和等于显示总量。
- 总量由完整响应的 API usage 校准，新增工具结果后的变化使用本地估算。分类始终是估算，不表示 API 提供了精确分类账单。容量来自 GET /models；元数据尚未获得时标为保守默认值。
- 占比超过 45% 后，仅在完整 response.completed 及当前整批调用/输出配对完成的边界压缩。独立无工具的 SDK 摘要请求完成后，检查摘要有效且释放空间，再原子替换历史。失败、断流或取消保留原历史。
- 保留最近三条用户输入原文、真实操作检查点、独立目标及逻辑引用。历史摘要是资料，不能改变工具权限，也不能复用旧的删除确认。压缩请求计入每轮 24 次模型调用及 64K 输出预算。
- `/compact` 空闲时压缩，执行中请求压缩则等待安全边界；`/goal` 设置并开始本次会话持续目标，可编辑、清除或停止后继续，新对话清空；会话及目标不落盘。
- `/` 与 `@` 菜单支持过滤、上下选择、Enter/Tab、鼠标点击、Escape；输入法合成期间隐藏菜单并禁止误发送。引用游戏/Java/账户只传现有业务入口注册的逻辑 ID 与显示信息，不传凭据，不切换当前配置，发送前检查引用有效性。

## 接口依据

- [DeepSeek Responses API](https://api-docs.deepseek.com/zh-cn/guides/responses_api/) 暂不支持 `context_management`，采用现有固定 OpenAI .NET SDK 的独立无工具摘要请求。仍要求完整 `response.completed`，保留常规工具循环的推理协议内容。
- [DeepSeek 模型列表](https://api-docs.deepseek.com/api/list-models/) 提供 `context_window` 与输出容量元数据。不把上下文容量硬编码为某个宣传值。

## 验证记录

- Release 构建：通过，应用代码无新增警告；四项既有 xUnit 分析器警告保留。
- 18 项针对性自动检查通过；覆盖安全压缩边界、失败/取消保留、目标/引用保留、统计校准、SDK 无工具摘要与既有 Agent 权限回归，以及 WPF 输入菜单/弹窗生命周期。结果在 `artifacts/open-source-check/ai-ui/ai-context-composer.trx`。
- 浅深主题：主聊天区与圆环使用离屏渲染检查（`artifacts/ui/AI-context-composer-{light,dark}.png`）；独立不可见 WPF 宿主验证 Popup 已加载后的悬停/固定/关闭、分类字段、菜单项目与深色背景，结果在 `artifacts/open-source-check/ai-ui/ai-context-visual.trx`。这不等同于人工桌面操作。
- 真实 DeepSeek API、长上下文实际压缩效果及人工中文输入法/鼠标验收：本次未实测。安装与游戏启动核心未变，不重复扩大验证矩阵。
- 交付：更新源码、Full/Lite 单文件 EXE 与源码 ZIP。两个 EXE 的 `--verify-package` 自检通过，记录在 `artifacts/open-source-check/ai-context-{full,lite}.json`；发布校验值见 `bin/release-manifest.json` 与 `bin/SHA256SUMS.txt`。
