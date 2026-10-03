# 下载模块更新

新增“下载”主导航，位于版本之后；包含游戏、Mod、资源包、光影和任务五个滑动页签。沿用页面缓存、虚拟列表和现有玻璃主题。

## 使用

- 游戏：搜索 Minecraft，选择正式版或快照，填写实例名称，选择加载器和 OptiFine，再安装。新实例默认使用独立游戏目录，成功后设为当前，保留在下载页。
- Forge / Fabric / NeoForge 互斥。Forge + OptiFine 只接受 OptiFine 明确声明的完整 Forge 版本，或可唯一解析的旧构建号。选择 OptiFine 后会选中对应 Forge；也可以切回“原版”安装独立 OptiFine。不允许 Fabric / NeoForge 与 OptiFine 混合。
- 内容：选择目标游戏与 Modrinth / CurseForge，搜索、选择匹配文件并安装。目标选择不改变当前启动版本。可用“更多”继续加载结果。光影没有检测到渲染组件时显示“下载文件”。资源与光影不会悄悄安装其他渲染 Mod。
- 任务顺序执行，显示阶段、进度、速度和连接详情，支持取消和重试；下载期间可以切换页面。
- 设置新增“下载”分组：BMCLAPI 游戏镜像、MCIM 内容镜像、CurseForge API Key。Key 通过 DPAPI 保存，不写入 settings.json，不转发给重定向域名。

## 实现

通用分段下载器复用 Java 下载实现：单文件最多四连接，文件任务内部并行，全应用文件下载共享八连接上限。不支持 Range 时单连接下载；连接停滞超时，游戏文件请求或校验失败后尝试官方源。Java 安装串行加锁，避免设置页与下载页重复写同一个运行时。

新实例在游戏根目录 `.ikun-downloads` 暂存、校验和安装，最后提交实例目录；同名拒绝覆盖。已有共享文件只在校验符合时复用，不修改其内容。取消清理本任务暂存；已提交的有效共享依赖保留。现代 Forge / NeoForge 使用本地安装器适配，可取消解压、复制和 Java 处理器，并核验处理器退出码与输出。旧 Forge 仍使用 CmlLib 原有格式适配。NeoForge 1.20.1 的旧格式暂未开放，界面在配置前排除。

Mod 安装检查平台的游戏版本、加载器、客户端标记、必需依赖和冲突，下载后解析 Fabric JSON、Forge / NeoForge TOML 与嵌套 JAR，检查已有启用 Mod 的依赖、游戏和 Java 约束。不覆盖同项目不同版本或同名不同内容。无法解释的必要约束会阻止提交。该检查覆盖声明的约束，不保证任意组合都没有运行时冲突。

元数据缓存、图标和安装记录位于 data/downloads。游戏文件和暂存位于游戏根目录，Java 位于配置的 runtime 目录。关闭或迁移数据时取消并等待任务。原有本地扫描及完整文件离线启动不增加版本清单请求。

## 验证记录

- 自动化：整个现有套件 75/75 通过；后续下载与离屏 UI 定向复查 21/21 通过，依赖客户端与中文路径校验调整后下载模块 12/12 通过。证据：tests/Launcher.Tests/TestResults/downloads.trx、downloads-final-focused.trx。
- 真实安装：Minecraft 1.20.1 原版、Forge 47.2.18、Fabric（当时目录最新加载器）、OptiFine HD_U_I6、Forge 47.2.18 + OptiFine HD_U_I6；另安装 Minecraft 1.21.1 + NeoForge 21.1.252。均成功。
- 真实启动：上述六种实例均创建游戏进程并达到渲染器/声音引擎初始化。现代安装适配和根目录暂存复查的 Forge、NeoForge、Forge + OptiFine 实例也通过启动检查。没有测试进入世界或长期游玩。
- 真实内容：通过 MCIM / Modrinth 下载并安装 Sodium 0.5.11，目标 Fabric / 1.20.1，本地元数据检查通过；已安装相同文件再次操作复用。CurseForge 经 MCIM 搜索返回 30 项，JEI 对应目标取得 25 个匹配文件。未配置真实 CurseForge 官方 API Key，因此官方认证下载未实测。
- 实际处理器取消：Java 测试处理器启动后取消，22ms 内结束并返回取消；仅终止本次子进程。
- UI：六入口、单页呈现、缓存、虚拟列表、设置定位与主题控制通过 WPF 离屏检查。未进行人工桌面交互、不同 DPI 的下载页和完整数据迁移中下载的桌面验收。
- 最后复查：BMCLAPI Forge 目录 + Forge 47.2.18 / OptiFine HD_U_I6 在“验证 镜像 forge-optifine”中文和空格路径安装成功；处理器输出校验已修正引号路径误判。
- 自包含发布包：复制到独立目录执行，实际创建主窗口，初始化日志出现“启动器就绪”；未把启动检查等同于人工桌面验收。证据：artifacts/package-downloads-smoke/result.json。
- 一次镜像请求曾在加载器依赖末尾停滞，补充超时/官方回退后本地暂存实装三例均成功。

真实安装与启动证据位于 artifacts/download-smoke；测试仅在独立目录写入，G:/MC/.minecraft 仅作为校验后复制的只读缓存，未修改原游戏。

## 构建与交付

环境：Windows x64，.NET SDK 9.0.201；沿用当前仓库 net9.0-windows。

```powershell
dotnet restore src/Launcher.App/Launcher.App.csproj
dotnet publish src/Launcher.App/Launcher.App.csproj -c Release -r win-x64 --self-contained true -o bin/publish-downloads
```

当前发布固定本机已缓存的 RuntimeFrameworkVersion=9.0.3，用户不需要另装 .NET。Java 和 Minecraft 文件不随包附送。直接运行 iKunLauncherNext.exe。发布包不包含测试游戏、账户、日志、API Key 或任何用户 data。

源码包不包含 bin、obj、测试游戏与实际账号。开源库及移植源码许可见 THIRD-PARTY-NOTICES.md 和 ThirdParty 中许可证。

## 来源

- [BMCLAPI 文档](https://bmclapidoc.bangbang93.com/) / [BMCLAPI 项目](https://github.com/bangbang93/BMCLAPI)
- [MCIM](https://github.com/mcmod-info-mirror/mcim-api)
- [Modrinth API](https://docs.modrinth.com/api/)
- [CurseForge API](https://docs.curseforge.com/rest-api/)
- [OptiFine 官方版本与 Forge 对应信息](https://optifine.net/downloads)

镜像与平台可用性取决于服务；无官方下载权限的 CurseForge 文件不推算地址绕过限制。OptiFine 没有在当前接口提供统一外部校验和：通过官方/镜像来源、JAR 结构及独立模式内部补丁 MD5 检查验证，未宣称具有官方签名证明。

