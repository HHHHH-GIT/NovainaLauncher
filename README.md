<div align="center">

![Novaina Launcher](docs/images/hero.jpg)

# Novaina Launcher

**从一句想法，到整个世界。**

面向 Windows 的 Minecraft Java 版启动器 · AI 助手 · 弹幕日志 · 3D 皮肤

[![Release](https://img.shields.io/github/v/release/HHHHH-GIT/NovainaLauncher?color=007AFF&label=下载)](https://github.com/HHHHH-GIT/NovainaLauncher/releases/latest)
[![MIT](https://img.shields.io/badge/License-MIT-AF52DE)](LICENSE)
![Windows](https://img.shields.io/badge/Windows-10%20%2F%2011-007AFF)
![WPF](https://img.shields.io/badge/.NET-9%20%7C%20WPF-512BD4)

[下载完整版](https://github.com/HHHHH-GIT/NovainaLauncher/releases/download/v1.0.0/NovainaLauncher-1.0.0-win-x64.exe) · [下载 Lite](https://github.com/HHHHH-GIT/NovainaLauncher/releases/download/v1.0.0/NovainaLauncher-1.0.0-win-x64-lite.exe) · [新手指南](docs/QUICKSTART.md) · [提交问题](https://github.com/HHHHH-GIT/NovainaLauncher/issues)

</div>

## 第一次使用

**推荐下载完整版：一个 EXE，放进可写文件夹，双击运行。** 支持 Windows 10 1809 及以上、Windows 11，x64。

| 版本 | 适合谁 | 需要另装 .NET 吗？ |
| --- | --- | --- |
| [完整版](https://github.com/HHHHH-GIT/NovainaLauncher/releases/download/v1.0.0/NovainaLauncher-1.0.0-win-x64.exe) | 第一次使用，开箱启动 | 不需要 |
| [Lite](https://github.com/HHHHH-GIT/NovainaLauncher/releases/download/v1.0.0/NovainaLauncher-1.0.0-win-x64-lite.exe) | 已有运行环境，希望减少下载体积 | 需要 .NET 9 **Windows Desktop Runtime x64** |

1. 在账户页添加微软、LittleSkin 或离线账户。
2. 在下载页选择游戏版本、加载器，给游戏起一个名字并安装。已有游戏可在版本页选择 `.minecraft` 或其父目录。
3. 安装时默认自动识别并准备对应的 Java；可在“设置 → 启动与Java”关闭自动下载，改用自己提供的运行环境。
4. 选择游戏，点击“启动游戏”。

Java 与游戏文件单独获取；3D 皮肤预览需要 [Microsoft WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)。AI 是可选功能，普通登录和启动无需配置 AI Key。

## 一句话，开始行动

打开顶栏 **AI Mode**，配置自己的 DeepSeek API Key，随后就可以用中文描述目标：

> “我想玩一个探索类整合包，帮我选一个并安装。”
>
> “给这个游戏装上钠，先帮我找合适的文件版本。”
>
> “刚才启动失败了，帮我看看日志。”
>
> “把当前游戏导出为整合包。”

Agent 查询本地状态、提出选择题、调用安装与管理工具，并用任务卡片展示真实结果。删除等敏感操作会先展示具体对象，等待你确认。当前支持单会话多轮对话，不保存历史会话。

模型旁的圆环显示上下文用量与分类估算，超过 45% 后在操作完成的安全边界自动压缩。输入 `/compact` 可主动压缩，`/goal` 可设置本次会话持续目标，`@` 可引用已识别的游戏、Java 与账户。连续工具调用集中展示，完成后自动折叠；回答过的问题展开时只保留你的答案。详细用法见 [新手指南](docs/QUICKSTART.md)。

![AI 问答与选择卡片](docs/images/ai.png)

*WPF 界面离屏截图，使用演示问题；不代表真实 API 操作验收。*

## 你的世界，由你组织

| 功能 | 可以做什么 |
| --- | --- |
| 游戏与加载器 | 安装原版、Forge、Fabric、NeoForge、OptiFine；支持明确匹配的 Forge + OptiFine |
| 模组与整合包 | 热门模组、中文关键词、文件版本筛选；在线整合包、ZIP/MRPACK 导入和本地导出 |
| 版本管理 | 单击切换、双击回首页；统一管理 Mod、存档、资源包和光影包 |
| 账户与皮肤 | 微软、LittleSkin、离线账户；皮肤头像、3D 预览和 PNG 导入 |
| 日志弹幕 | 精选/全部、屏蔽规则、三种样式；完整日志同步保留 |
| 运行环境 | Java 自动选择、独立 runtime、智能内存与每版本配置 |
| 下载任务 | 国内镜像优先、校验与来源回退、取消、稳定的总进度和连接详情 |
| 界面 | 浅深主题、玻璃材质、两种导航、三档动画和像素超新星开场 |

![下载 Welcome 页面](docs/images/downloads.png)

*WPF 下载页离屏截图。下载、搜索与安装由独立业务服务执行。*

## 让日志与形象，也有自己的表达

弹幕让关键阶段、警告和游戏输出融入界面；屏蔽只影响展示，不删完整日志。皮肤预览支持旋转、缩放、经典与纤细模型。

![3D 皮肤预览](docs/images/skins.png)

*皮肤功能宣传示意，使用 HTML/skinview3d 重现；不是实际账户录屏。LittleSkin 换皮请到官方网页，随后在启动器刷新；离线皮肤用于启动器预览。*

![像素超新星开场动画](docs/images/startup.gif)

*实际 WPF 开场动画的桌面采样。动画遵循设置中的舒缓、性能与关闭选项。*

## 常见问题

**已有游戏怎么接入？** 在版本页选择现有 `.minecraft` 或父目录并刷新；共享/隔离目录遵循版本设置。安装前请确认目标目录。

**Lite 打不开？** 安装 [.NET 9 Windows Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/9.0)，或直接使用完整版。普通 .NET Runtime 不包含 WPF 桌面运行库。

**数据保存在哪里？** 默认是 EXE 同目录的 `data`，包含配置、加密账户、日志与缓存；`runtime` 放 Java，`.minecraft` 放游戏。可在设置更改数据目录。不要把整个 data 发到 Issue；DPAPI 账户令牌绑定当前 Windows 用户。

**下载源与兼容性？** 游戏优先 BMCLAPI，内容优先 MCIM，失败时按配置回退。Mod 文件由你选择，平台标签供参考；启动器不会保证任意 Mod 组合没有运行冲突。

**AI Key 会公开吗？** Key 在本机由 Windows DPAPI 加密；AI 请求发送至所配置的 DeepSeek 服务。日志分析会发送脱敏后的必要内容，请勿在聊天中输入密码或令牌。

## 从源码构建

需要 Windows、Git 和 .NET 9 SDK（`global.json` 指定 9.0.201，允许更新 feature band）。Node.js 仅宣传片工程需要。

```powershell
git clone https://github.com/HHHHH-GIT/NovainaLauncher.git
cd NovainaLauncher
dotnet build iKunLauncherNext.slnx -c Release
dotnet run --project src/Launcher.App/Launcher.App.csproj -c Release
```

生成完整与 Lite 单文件包：

```powershell
./scripts/Publish.ps1
```

脚本生成源码 ZIP 需要 Python 3.11+。产物在 `bin`，不参与 Git 提交。内部历史解决方案名仍为 `iKunLauncherNext`，产品名为 Novaina Launcher。

| 路径 | 内容 |
| --- | --- |
| `src/Launcher.Core` | 启动、认证、Java、下载、安装与内容服务 |
| `src/Launcher.AI` | DeepSeek 会话、提示词、工具与权限 |
| `src/Launcher.App` | WPF 页面、MVVM、动画和共享操作入口 |
| `tests/Launcher.Tests` | 自动化检查 |
| `docs` | 使用、构建、发布和历史记录 |
| `spec` | 开发计划与架构规范 |
| `scripts` | 发布、源码打包和图标工具 |
| `promo` | Remotion 宣传片源码，商业配乐不分发 |

更多说明：[文档索引](docs/README.md) · [构建与发布](docs/BUILD.md) · [开发约定](AGENTS.md) · [当前架构](spec/architecture.md)。

## 参与与许可

欢迎提交 Issue 和 Pull Request。反馈时附启动器版本、Windows 版本、复现步骤及脱敏后的错误；说明是本地扫描、下载、安装还是启动阶段。

原创项目代码采用 [MIT](LICENSE)。第三方库、Minecraft 素材与加载器标志保留各自许可，见 [第三方声明](docs/THIRD-PARTY-NOTICES.md)。Minecraft 为 Mojang/Microsoft 的产品；本项目为独立社区项目。

1.0.0 发布检查区分构建、离线资源检查、真实联网流程和人工桌面验证，详见 [发布记录](docs/releases/v1.0.0.md)。
