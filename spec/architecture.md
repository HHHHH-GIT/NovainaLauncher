# Novaina Launcher 架构规范

## 分层

```text
WPF/MVVM 普通页面       AI 时间线与人工交互
        │                       │
        └── LauncherOperations ──┤
                                AgentSessionService / AgentToolRegistry
        │                       │
Launcher.Core 业务服务       DeepSeek Responses 客户端
        │
CmlLib / 原生加载器安装器 / Downloader / DPAPI
```

UI 消费结构化状态，核心服务负责业务。Agent 使用相同的业务能力，不直接拼接命令或写任意文件。启动器不开放外部工具服务器。

## 核心模块

| 模块 | 主要入口 |
| --- | --- |
| 数据与加密 | `Storage.cs`、`DataDirectoryService.cs` |
| 账户与续期 | `AccountService.cs`、`AccountService.LittleSkin.cs` |
| 版本与内容 | `VersionService.cs`、`VersionManagementService.cs`、`VersionContentService.cs` |
| Java 与内存 | `JavaService.cs`、`MemoryService.cs` |
| 安装与校验 | `DownloadInstallService`、`HostedGameInstallEngine`、`NativeGameInstallEngine`、`InstallationVerifier` |
| 传输与任务 | `SegmentedDownloader.cs`（Downloader 适配）、`DownloadConnectionBudget.cs`、`DownloadTaskService.cs` |
| 目录与缓存 | `DownloadCatalogService.cs`、`ModReleaseCache.cs`、`ChineseModSearchIndex.cs` |
| 皮肤 | `IAccountSkinService`、`AccountSkinService`、`SkinPreview` |
| AI 工具 | `ILauncherOperations`、`AgentToolRegistry`、`AgentPrompt`、`AgentPathGuard` |

## 安装闭环

提交时冻结账户、版本、Java、游戏目录及名称；网络和文件操作支持取消。安装先在暂存区域进行，校验完整继承链及必需文件后提交实例。实例显示名称与安装父版本分离，辅助目录不作为重复游戏列出。

镜像优先，失败/校验错误按策略回退。单文件最多四连接，共用八连接预算。任务进度展示区分单调的父进度和当前子阶段；详情只有一个展开入口。

Mod 下载提供全量文件、本地版本/加载器筛选及目标选择，用户决定兼容性。保留原生安装器及 Forge + OptiFine 明确对应等必要检查。

## AI 权限与会话

工具参数 Schema 关闭额外字段，以真实查询结果的 ID 引用游戏/账户/文件。默认审批模式下，正常查询、安装、选择和启动自动执行；删除等敏感动作在具体审批卡片确认后执行，选择器授权导入导出路径。授权模式由用户在输入区确认警告后启用，开放完整终端、任意文件操作与直接路径导入导出，不再逐项审批或限制项目目录；仍受当前 Windows 用户权限限制。模型及历史记录不能开启授权模式，切换会话、退出 AI 或重启恢复审批模式。

问题、审批、取消均可挂起会话；基础模式与工作台分别加密保存到 data/ai/conversations，恢复时不重放旧调用或沿用批准。工具结果与推理协议继续传回模型，用户只看到消息和行动摘要。任务组隔离 AI 与普通任务；认证由人工登录与已有续期服务负责。

工作台采用独立的提示词、工具目录、项目授权和受限开发终端，基础业务仅通过明确的基础子代理委托。AI 配置独立保存，普通设置不显示 AI 项。开发终端不是操作系统沙箱，审批模式构建需信任确认；授权模式使用宿主确认的免审批策略。详情见 [AI 工作台规范](AI-WORKBENCH.md)。

## UI 与资料

固定顶栏、两种导航、主题与动画统一由控件/资源策略管理。页面缓存、虚拟列表、像素滚动，动画首帧和中断必须保持一个有效页面。

规范放 spec，操作文档和验证记录放 docs。运行数据、凭据、游戏、Java、发布文件不入 Git。MIT 只覆盖本项目原创内容，第三方许可随内嵌素材保留。
