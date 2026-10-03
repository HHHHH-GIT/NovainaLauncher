# 下载与安装重构

2026-10-02 的后续调整见 `DOWNLOAD-HOTFIX-20261002.md`：模组改为手动选择全部版本，取消原先的兼容检查与自动依赖流程；版本列表隐藏安装父依赖，并修正入口动画首帧。下面保留上一轮实现与验收的历史记录。

## 使用与构建

Windows x64 自包含包：`bin/iKunLauncherNext-win-x64-welcome.zip`。解压后运行 `iKunLauncherNext.exe`，无需另外安装 .NET。Java 和游戏文件仍按原有设置识别或下载。

源码包：`bin/iKunLauncherNext-source-welcome.zip`。当前项目沿用 .NET 9 / WPF，与现有登录、主题、版本管理保持一致。

```powershell
dotnet restore iKunLauncherNext.slnx
dotnet publish src/Launcher.App/Launcher.App.csproj -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=9.0.3 -p:Optimize=true -o bin/publish-welcome
```

发布包不携带账户、令牌、用户设置、Java 或 Minecraft 文件。更新既有安装时，保留该目录中的 `data`、路径引导文件、`runtime` 和 `.minecraft`；程序文件可替换。源码构建目录与发布目录的数据位置相互独立。

## 实现

- 下载入口改为 Welcome：两张长按钮进入“游戏版本”或“模组”，大标题、依次飞入、局部高光、悬停抬升与按压反馈；四步流程使用可中断的横向位移与淡入，服从三档动画和系统动画设置。
- 缓存页面、选择与滚动状态，只保留当前步骤参与布局。资源包和光影下载入口移除；任务由右上角打开应用内面板。
- 任务详情固定 600×480 DIP，只按主窗口空间收缩；连接按稳定标识更新，内容内部滚动，避免连接数变化造成尺寸跳动。状态更新限速，处理与校验阶段不显示虚假的下载百分比。
- 游戏使用 CmlLib.Core 4.0.6、Forge 1.1.1、NeoForge 4.0.1 原生接口，Fabric 使用核心安装器。删除手写 Forge / NeoForge 处理器执行链，保留 OptiFine 上游本地 JAR 补丁适配。
- 下载统一使用 Downloader 5.9.8；单文件最多四连接，全应用八连接预算。临时文件经声明大小与 SHA 校验后提交，最多三次完整尝试，支持源回退、取消与失败清理。
- 安装宿主隐藏运行，任务专属 Job Object 管理宿主与安装器后代进程。取消先请求收尾，超时结束该任务的进程树；已有 Minecraft 不归入该 Job。
- 暂存安装后，解析完整继承链并逐项检查客户端、资源索引、资源对象、库与当前平台所需原生库；损坏文件补全并复检。处理器输出按声明的 SHA-1 验证，无公开哈希的文件按 ZIP / JSON 结构验证。
- 保留原始 Minecraft / 加载器版本目录；自定义名称通过继承实例关联，避免移动父版本 JAR。同名实例拒绝覆盖，共享损坏文件通过验证后原子替换。
- 就绪后后台预取游戏清单与 30 个热门模组，分别按一小时 / 十五分钟刷新，离线保留缓存。搜索防抖 350ms，回车立即执行，旧请求取消并防止过期结果覆盖。
- 移除原版目标误用的 `categories:minecraft` 条件，浏览默认不按目标过滤；空查询按下载量、搜索按相关性。加载器筛选仅使用真实加载器。
- 模组确认前冻结安装清单：解析平台依赖、实际 JAR 元数据和已有启用 Mod，补入实际必需的依赖，检查版本 / 加载器 / Java / 冲突。修复合法嵌套 API 组件的重复误报，以及 Fabric 空预发布边界约束的解析。
- Forge + OptiFine 保留精确对应检查，Fabric / NeoForge + OptiFine 组合仍禁用。账户、数据目录、版本管理配置保持原位。

## 验证记录

自动化 **80 / 80 通过**。覆盖下载断流、Range 回退、错误哈希、来源回退、取消清理、继承链缺失文件补全、Mod 必需依赖 / 冲突 / 嵌套组件、搜索过滤、页面缓存和任务详情稳定尺寸等。本轮完整运行约 47 秒。

在独立 `artifacts/welcome-smoke/.minecraft` 中完成真实下载、原生安装和提交，复用已校验缓存：

| 配置 | 完整性复检 | 实际进程与渲染初始化 |
| --- | --- | --- |
| 原版 1.20.1 | 3643 / 3643 | 通过 |
| Forge 47.2.18 / 1.20.1 | 3673 / 3673 | 通过 |
| Fabric / 1.20.1 | 3652 / 3652 | 通过 |
| NeoForge 21.1.252 / 1.21.1 | 3999 / 3999 | 通过 |
| OptiFine HD_U_I6 / 1.20.1 | 3646 / 3646 | 通过 |
| Forge 47.2.18 + OptiFine HD_U_I6 | 3673 / 3673 | 通过 |

热门空查询、Sodium 搜索及原版目标浏览均实际返回 30 项。Indium 1.0.34 的清单自动补入 Sodium 0.5.11 与 Fabric API 0.92.2，三个文件通过元数据检查后一起安装；该 Fabric 实例也实际进入渲染初始化。启动探针随后仅结束测试游戏进程，没有进行存档内游玩验收。

原始证据保留在：`artifacts/welcome-smoke/mirror-results.json`（仅安装结果，启动字段是安装探针占位）、`welcome-launch-results.json`（实际启动结果）、`welcome-content-results.json`、`data/downloads/installs` 和 `launch-logs`。证据目录不进入发布包或源码包。

UI 使用 WPF 离屏渲染及真实 HWND 自动检查：浅深主题、八个步骤只显示一页、缓存恢复、连接数量 1→4→8→2→1 时详情始终 600×480 且原位保留连接行。预览在 `artifacts/ui/Welcome-Light.png` 与 `Welcome-Dark.png`。

最终 Release 自包含目录中的 `iKunLauncherNext.exe --install-host` 另行完成原版 1.20.1 的实际安装、3643 项复检和提交，证明发布宿主及依赖可运行；记录在 `artifacts/welcome-release-smoke`。该检查仍使用独立目录与只读缓存。

尚未人工桌面验收动画手感、各 DPI 和拖动效果；旧 Forge 安装格式、全冷缓存六组合矩阵，以及安装器处理阶段取消的人工验证未完成，均不标记通过。实际安装 / 下载与进程初始化结果不等同于全版本、全 Mod 组合的运行兼容保证。

## 来源与许可

- [CmlLib 核心与安装器文档](https://cmllib.github.io/CmlLib.Core-wiki/en/)
- [Downloader 5.9.8](https://www.nuget.org/packages/Downloader/5.9.8)
- [Modrinth 搜索接口](https://docs.modrinth.com/api/operations/searchprojects/)
- [Apple Onboarding](https://developer.apple.com/design/human-interface-guidelines/onboarding)

依赖及移植代码说明见 `THIRD-PARTY-NOTICES.md`；发布包保留 `ThirdParty` 许可证。
