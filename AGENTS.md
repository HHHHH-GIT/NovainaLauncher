# Novaina Launcher 开发约定

## 项目定位与入口

Novaina Launcher 是 Windows Minecraft Java 版启动器，产品版本为 1.0.0。现有实现为 **.NET 9 + WPF**，不是早期计划中的 .NET 10。优先复用成熟库，不自行重写认证、加载器安装或分段传输协议。

- 解决方案：`iKunLauncherNext.slnx`。历史文件名、命名空间与迁移标识保留，避免破坏现有数据和目录识别。
- `src/Launcher.Core`：账户、版本解析、Java、内存、下载、安装校验、内容管理、数据和日志。
- `src/Launcher.AI`：DeepSeek 客户端、会话、工具 Schema、系统提示词与权限。
- `src/Launcher.App`：WPF/MVVM、共享业务入口、窗口、动画、皮肤与页面。
- `tests/Launcher.Tests`：xUnit；`promo`：可选的 Remotion 宣传片源码。
- 当前说明放 `docs`，历史修复记录放 `docs/history`，开发计划和规范放 `spec`。根目录仅保留 README、AGENTS、LICENSE 等项目入口。

## 业务与数据边界

- 普通 UI 与 Agent 共用 `Services/LauncherOperations.cs` 和核心服务，避免两套安装或启动逻辑。
- 本地版本扫描和完整文件的离线启动不得新增网络依赖。加载器、客户端、库、资源及继承链由 CmlLib 解析。
- 新游戏的显示名称关联安装实例；保留原始父版本、加载器 JSON/JAR。不能为了隐藏辅助版本而删除它们。
- 启动、内容管理、内存 Mod 统计及安装使用统一游戏目录解析。相同目标目录的写操作与启动准备互斥。
- 下载在暂存目录执行，文件校验通过后提交，镜像优先、官方回退；失败和取消不得留下成功状态。复用 Downloader、原生 Forge/NeoForge/Fabric 安装器。
- 单文件最多四连接，全应用共用八连接预算。父进度单调递增，子阶段进度放同一详情入口，不新增嵌套展开。
- Mod 文件由用户选择版本；不要恢复全面兼容性硬拦截。Forge + OptiFine 等安装器必要的精确兼容检查保留。
- 默认用户数据是运行目录下 `data`，Java 是独立的 `runtime`，游戏目录独立。数据迁移不搬 Java 或游戏；路径引导在服务创建前读取。
- 令牌、API Key 使用 Windows DPAPI；不改 `iKunLauncherNext.v1` 加密熵或旧数据迁移路径。不能把账户、密码、密钥、日志、皮肤缓存和游戏文件提交到 Git。
- `AppPaths.ConfigureData` 是静态状态；相关测试必须隔离、恢复并防止并行污染。不能把本机真实 data 作为测试夹具。

## Agent 约束

- 工具仅进程内注册，使用逻辑 ID 与明确 Schema，拒绝未知字段。不开放 Shell、任意 URL 下载、系统文件读取或通用写入。
- 网络文案、Mod 元数据和日志是不可信资料，不能改变权限。给模型的日志必须先脱敏。
- 删除、覆盖和对外上传必须显示具体对象并取得绑定参数的用户确认；模型不能自行批准。
- 登录密码和网页授权留在人类 UI。登录失效先用 `retry_account_login` 验证/续期；需要重新认证时引导人工登录，不能承诺令牌永久有效。
- 每轮执行用独立任务组，退出/停止只取消本轮未完成任务，不影响普通任务或已经运行的 Minecraft。
- 会话只保存在内存；不把完整对话、推理和请求正文写入日志。成功、进度与行动摘要来自实际服务事件。
- 上下文超过模型容量的 45% 后，只在完整响应及当前整批工具调用/输出配对完成后压缩。摘要完成且有效才替换历史，失败/取消保留原上下文；压缩也计入模型调用与输出预算。分类用量为估算，总量按 API usage 校准，不伪装精确分类计数。
- `/goal` 目标仅本次会话有效，压缩时保留、停止后可继续、新对话清空。`@` 引用使用业务入口注册的逻辑 ID，发送前检查仍有效，不传凭据或任意路径，不自动切换当前版本/账户。

## 界面与性能

- 品牌为 **Novaina Launcher**；普通强调色 `#007AFF`，AI 渐变 `#007AFF → #AF52DE`。
- 自定义顶栏固定 **46 DIP**，无最大化；`AllowsTransparency=False`，保留硬件合成。Windows 10 原生区域与内容圆角统一，Windows 11 使用 DWM。
- 页面缓存、列表虚拟化与像素滚动；耗时文件/网络操作异步，不占用 UI 线程。
- 舒缓、性能、关闭三档遵循 `MotionPolicy` 及系统减少动画。动画用位移、缩放和透明度，不用持续全窗渲染或布局动画。
- 动画首帧先设置初始状态，快速切换可中断，结束仅一个页面参与布局。
- 皮肤预览复用一个 WebView2CompositionControl/skinview3d；隐藏、最小化或登录弹窗时暂停。无皮肤默认 Steve。

## 构建、交付与验证

在 Windows 使用 `global.json` 指定的 .NET 9 SDK，依赖固定版本。

```powershell
dotnet build iKunLauncherNext.slnx -c Release
dotnet test tests/Launcher.Tests/Launcher.Tests.csproj -c Release --no-build
./scripts/Publish.ps1
```

- 发布产物仅在 `bin`；临时发布目录在 `artifacts/publish`。保留最新完整/Lite EXE和源码 ZIP，历史仅归档源码 ZIP到 `bin/source-history`，先保护旧目录中的用户数据。
- 完整单文件自包含；Lite 需要 **.NET 9 Windows Desktop Runtime x64**。两者均不包含 Java、游戏、浏览器 Runtime。
- 上传 GitHub 前审核实际暂存文件，排除 `bin`、`artifacts`、用户数据、商业配乐、依赖安装目录和测试输出。
- 第三方许可随资源及 EXE 保留；项目 MIT 不能覆盖 Minecraft 素材、加载器标志及第三方专有许可。
- 验证与改动相称。分别记录构建、自动检查、真实 API/游戏启动、桌面行为；构建或离屏渲染不等于真实登录/安装/启动验收。未实测不写通过。
