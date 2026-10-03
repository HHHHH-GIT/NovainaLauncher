# Novaina Launcher 1.0.0 · 国内镜像与单文件发布

日期：2026-10-03。

## 国内下载

- 游戏清单、客户端、资源、依赖库、Forge/Fabric/NeoForge 使用 BMCLAPI 优先；模组文件与图标使用 MCIM 优先，补齐 CurseForge 的 mediafilez/mediafile 文件域名。
- 启动时补全缺失依赖也使用当前下载源设置，避免安装走镜像而首次启动走官方。
- Java 优先获取 Adoptium JRE，文件优先清华 Adoptium 镜像；无 JRE 时使用 JDK。authlib-injector 文件优先 BMCLAPI。
- 新配置的游戏/内容源默认镜像优先；保留已有用户明确选择的官方源设置。
- 同一任务短期复用成功的镜像节点，减少分段请求反复经过镜像重定向。节点失效后重新发现，随后可回退官方。
- 一般失败前两次仍允许镜像，最后尝试官方；声明 Hash 校验失败立即回退官方。文件大小、Hash、暂存写入及取消清理继续保留。
- 下载源日志使用 Novaina 名称，显示实际镜像/节点。

来源：[BMCLAPI](https://bmclapidoc.bangbang93.com/)、[MCIM](https://github.com/mcmod-info-mirror/mcim-rust-api)、[清华 Adoptium 镜像](https://mirrors.tuna.tsinghua.edu.cn/help/Adoptium/)。镜像可能因维护、文件尚未同步或限流而回退；本次没有声称已验证国内直连带宽。

## 品牌

- 产品、窗口标题、顶栏、EXE 元数据及导出声明统一为 **Novaina Launcher**。
- 新图标为蓝紫像素/体素星芒与星环，透明背景，替换应用 ICO 和顶栏 PNG。顶栏仍为 46 DIP，图标为 16 DIP。
- 生成工具、完整提示词和尺寸说明：`src/Launcher.App/Assets/Brand/GENERATED-ICON.md`。
- 保留原有数据目录、路径引导文件、DPAPI 加密标识与内部安装协议，避免更名破坏已有账户。没有迁移或覆盖用户的游戏及 Java。

## 单文件发布

| 文件 | 大小 | 要求 |
| --- | --- | --- |
| `NovainaLauncher-1.0.0-win-x64.exe` | 87.21 MiB | Windows x64，无需安装 .NET |
| `NovainaLauncher-1.0.0-win-x64-lite.exe` | 54.56 MiB | Windows x64，需 .NET 9 Windows Desktop Runtime |

两个发布目录都仅包含一个 EXE，可直接分发。Java 与 Minecraft 文件另外获取；运行仍会创建 data、runtime 等目录。原生 DLL 由 .NET 的单文件机制解包，内嵌皮肤预览脚本、Steve 纹理和许可证按需释放到 data/assets。3D 预览使用系统 WebView2 Evergreen Runtime，缺失时显示原有安装入口。

完整版开启单文件压缩并内置 .NET/WPF；轻量版不带 .NET。NET SDK 不支持对框架依赖单文件启用这种压缩，所以轻量版不启用该选项。两版均不强制裁剪 WPF/XAML，以免破坏运行行为。

体积并非主要来自业务代码或图标：WPF/.NET、Windows SDK、Skia、WebView2 SDK、UI 与安装依赖占大部分。低于 10 MB 的交付方向可以是依赖系统运行时、原生界面，或小型引导程序在首次运行时下载组件；引导程序只是把体积转移到首次下载。本次没有改换界面技术或引入新引导程序。

单文件机制及体积取舍：[Microsoft 部署说明](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)。

## 构建与验证

现有项目是 .NET 9 WPF，使用仓库 global.json 指定的 SDK。顺序发布两种 EXE 并生成源码 ZIP：

```powershell
./artifacts/Publish-Novaina.ps1
```

也可以分别发布：

```powershell
dotnet publish src/Launcher.App/Launcher.App.csproj -c Release -r win-x64 --self-contained true -p:PublishProfile=SingleFile -p:PublishSingleFile=true -o bin/publish-novaina-portable
dotnet publish src/Launcher.App/Launcher.App.csproj -c Release -r win-x64 --self-contained false -p:PublishProfile=SingleFileLite -p:PublishSingleFile=true -o bin/publish-novaina-lite
```

实际单文件离线检查：从只含该 EXE 的目录运行 `--verify-package`。该检查使用临时数据、禁止网络、离屏渲染四个页面，检查内嵌资源、Skia 原生加载、Steve 头像和 WebView2 Runtime 检测；不打开窗口、不读取真实账户。检查输出 JSON，成功退出码为 0。

已记录：两版发布通过且没有构建警告；两版实际 EXE 的上述离线检查均成功，正常退出；发布检查发现并修复皮肤服务重复释放导致的退出异常。镜像映射、重定向复用、Range 保留、回退、Java 失败清理、皮肤缓存及页面渲染等 26 项集中检查全部通过。

真实端点检查：BMCLAPI 版本清单、MCIM Sodium 搜索及清华 Java 21 JRE 的 HEAD 均返回 HTTP 200，结果记录于 `artifacts/novaina-mirror-endpoint-check.json`。当前机器网络可能经过代理，此记录证明端点可用，不证明中国用户的下载速度。

本次未做真实账号登录、完整游戏安装启动、3D WebView 渲染或人工桌面验收。旧账户、真实 API Key、游戏文件与 Java 不参与源码打包。

包大小及 SHA-256：`bin/novaina-release-check.json`。
