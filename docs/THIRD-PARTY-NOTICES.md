# Novaina Launcher 开源依赖与来源声明

本次保留当前 CmlLib.Core 4.0.6；新增依赖均固定版本。

AI 工作台的九篇内置开发指南为原创整理，引用的官方技术文档与链接保留原权利，不复制完整上游文章。运行时获取的 Fabric 示例、Forge MDK、NeoForge MDKs 与 Gradle Wrapper 存于用户授权项目，不内嵌到启动器；初始化保留各模板 LICENSE/NOTICE，生成项目及依赖仍遵循各自许可。源码处理、ZIP 解包、进程管理使用 .NET API，本次没有增加终端/Agent 框架 NuGet 依赖。

项目原创代码采用根目录 [MIT LICENSE](../LICENSE)。下列上游代码、库和素材保留各自许可，项目 LICENSE 不覆盖这些独立权利。相关 LICENSE/NOTICE 在 `src` 的 ThirdParty 子目录保留，并内嵌到单文件 EXE。

| 依赖 | 版本 | 许可 |
| --- | --- | --- |
| CmlLib.Core | 4.0.6 | MIT |
| Downloader | 5.9.8 | MIT |
| CmlLib.Core.Auth.Microsoft | 3.3.1 | MIT |
| CmlLib.Core.Auth.Microsoft.MsalClient | 2.0.0 | MIT |
| CmlLib.Core.Installer.Forge | 1.1.1 | MIT |
| CmlLib.Core.Installer.NeoForge | 4.0.1 | MIT |
| Tomlyn | 0.19.0 | BSD-2-Clause |
| Semver | 3.0.0 | MIT |
| WPF UI | 4.3.0 | MIT |
| OpenAI .NET SDK | 2.14.0 | MIT；用于 DeepSeek Responses 接入，LICENSE 随包保留 |
| Markdig | 1.3.2 | BSD-2-Clause；AI Markdown 解析，LICENSE 随包保留 |
| CommunityToolkit.Mvvm | 8.4.2 | MIT |
| SkiaSharp / SkiaSharp.NativeAssets.Win32 | 3.119.1 | MIT；原生第三方声明随包保留 |
| skinview3d | 3.4.2 | MIT；本地 bundle 与 LICENSE 随包保留 |
| Three.js / skinview-utils | 0.156.1 / 0.7.1 | MIT；包含于 skinview3d bundle，许可随包保留 |
| Microsoft.Web.WebView2 | 1.0.4258.31 | Microsoft WebView2 SDK License；LICENSE / NOTICE 随包保留 |
| Microsoft.Windows.SDK.NET.Ref | 10.0.17763.57 | [Windows SDK 许可](https://aka.ms/WinSDKLicenseURL)；Composition 控件运行依赖 |
| Microsoft.Extensions.DependencyInjection | 10.0.12 | MIT |
| System.Security.Cryptography.ProtectedData | 9.0.2 | MIT |
| XboxAuthNet.Game.Msal | 0.1.3 | MIT |

传递依赖还包含 HtmlAgilityPack、MSAL、XboxAuthNet、微软基础库；完整版本见项目 restore 资产。单文件版的依赖清单、皮肤预览资源与许可证随 EXE 内嵌，预览首次使用时解包到 data/assets。

移植源码：

- [mzggr0914/Optifine.Installer](https://github.com/mzggr0914/Optifine.Installer)，MIT，提交 4d84aff1f8d80ed298ca5141f25806cf6f9c417e。保留 patcher、Xdelta、资源读取工具，增加取消与资源释放；自行实现本地 JAR 安装入口，不调用其无法取消的下载入口。许可在 ThirdParty/Optifine/LICENSE。
- [CmlLib/CmlLib.Core.Installer.Forge](https://github.com/CmlLib/CmlLib.Core.Installer.Forge) 与 [Gml-Launcher/CmlLib.Core.Installer.NeoForge](https://github.com/Gml-Launcher/CmlLib.Core.Installer.NeoForge)，本次直接调用固定版本的原生安装器；已删除此前移植的处理器执行链。保留上游许可在 ThirdParty/Forge/LICENSE.txt 与 NEOFORGE-LICENSE.txt。
- [bezzad/Downloader](https://github.com/bezzad/Downloader)，使用 NuGet 5.9.8，下载适配层负责来源、连接预算、临时文件和校验；传输由上游库处理。许可在 ThirdParty/Downloader/LICENSE。
- [mono/SkiaSharp](https://github.com/mono/SkiaSharp)，固定 NuGet 3.119.1，用于 PNG / WebP 等模组图标解码为缓存缩略图。许可及原生依赖声明在 ThirdParty/SkiaSharp。
- [xoofx/markdig](https://github.com/xoofx/markdig)，固定 NuGet 1.3.2，用于 CommonMark 解析；WPF 轻量渲染适配不执行 HTML 或远程图片，仅允许用户点击 HTTP/HTTPS 链接。许可在 ThirdParty/Markdig/LICENSE。

中文模组搜索使用公开的 [HMCL 模组名称表](https://github.com/HMCL-dev/HMCL/blob/main/HMCL/src/main/resources/assets/mod_data.txt)，原始数据来自 MC 百科，权利属于其对应权利人。运行时拉取并缓存到用户数据目录，发布包不打包整份百科数据；未复制 HMCL 的 GPL 搜索实现。内置少量常见名称与简称用于首次加载和离线解析。

既有加载器图标的许可与出处见 [LOADER-ICONS.md](LOADER-ICONS.md)、[loader-icons.json](loader-icons.json) 与 [asset-licenses](asset-licenses)。

账户预览使用 [skinview3d](https://github.com/bs-community/skinview3d) 官方 3.4.2 npm bundle，未重写模型与渲染器。包内含 Three.js 与 skinview-utils；许可在 ThirdParty/skinview3d、ThirdParty/three、ThirdParty/skinview-utils。WebView2 使用 Composition 控件，系统 Evergreen Runtime 由 Microsoft 提供，不随包复制浏览器运行时。Windows SDK 的 C#/WinRT 投影通过 Windows 10 1809 目标框架提供，继续兼容本机 Windows 10 22H2。

默认 Steve 图片来自 Minecraft 1.21.1 的 `assets/minecraft/textures/entity/player/wide/steve.png`，通过 [minecraft-assets](https://github.com/InventivetalentDev/minecraft-assets/tree/1.21.1) 获取。该游戏素材的权利属于 Mojang / Microsoft，仅作为未配置皮肤的预览；不属于 skinview3d 的 MIT 许可。皮肤协议参考 [CmlLib/MojangAPI](https://github.com/CmlLib/MojangAPI/blob/master/MojangAPI/Mojang.cs) 与 [LittleSkin API](https://manual.littleskin.cn/advanced/api)，使用已有登录服务及可取消 HTTP 适配，没有引入旧版 MojangAPI NuGet 包。

下载的 Minecraft、OptiFine、Mod、资源包、光影包各遵循其发布者的许可及授权，本启动器发布包不分发这些游戏和内容二进制。下载地址和兼容信息来自 BMCLAPI / 官方、MCIM / Modrinth / CurseForge；不代表这些项目为本启动器背书。

整合包适配遵循 [Modrinth MRPACK 格式](https://support.modrinth.com/en/articles/8802351-modrinth-modpack-format-mrpack) 与 [CurseForge API](https://docs.curseforge.com/rest-api/)。游戏和加载器仍由上述 CmlLib 安装器处理，传输由 Downloader 处理；清单及 ZIP 使用 .NET JSON / ZIP API。整合包及其中内容的许可由对应发布者决定，本发布包不包含在线整合包或用户导出的内容。
