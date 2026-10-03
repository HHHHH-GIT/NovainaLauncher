# 按钮、数据目录与弹幕设置

2026-10-01。

## 更新内容

- 普通按钮移除重复描边，仅保留一层 1 DIP 浅灰边。浅色悬停边界减弱，指针高光与按压反馈保留。页签使用同一柔和边缘资源，未增加按钮模糊阴影。
- 设置增加“弹幕”“数据”定位项，沿用单个滚动页面和页面缓存。修复布局刷新覆盖定位选中项，以及接近底部的分组无法保持选中状态的问题。
- 弹幕显示改为关闭／精选／全部滑动页签，与顶栏快捷按钮同步。增加琉璃笺（卡片）、墨流（较大粗体）、虹渡（静态彩虹渐变）三种样式。
- 屏蔽预设支持多选 log4j、authlib-injector、warn、error，默认不勾选。前两项忽略大小写、匹配消息包含；后两项匹配日志等级。自定义精确匹配针对完整消息正文，正则匹配正文；两种匹配均忽略大小写。无效正则不启用，表达式执行超时后在当前规则快照中跳过该表达式。
- 过滤仅影响弹幕，日志页和完整日志文件照常接收已脱敏内容。修改模式、规则或样式清空待显示与正在显示的旧弹幕；保留轨道、队列限制和暂停规则。

## 数据保存

默认使用程序所在目录下的 data，包含 settings.json、DPAPI 加密账户和令牌、logs、认证注入器及启动器缓存。皮肤缓存如有也随该目录保存。launcher-paths.json 位于程序目录，默认记录相对路径 data，自定义位置记录绝对路径。

首次运行新版时，如果默认 data 为空，会复制原 LocalAppData/iKunLauncherNext/data 的启动器数据。原文件保留；其中 runtime 不复制，已有 Java 保留在原位置并继续扫描。新增 Java 默认下载到程序目录下独立的 runtime，也可以继续使用已配置的下载目录。

修改数据位置要求选择独立的空目录，完成任务取消、设置保存和日志收尾后，后台复制到暂存目录，成功后切换引导文件并重启。Java 和当前游戏目录不复制；失败保留原数据和活动路径。操作期间暂停内存定时更新，避免复制时再次写入配置。目录复制不改变 DPAPI 的当前 Windows 用户加密方式，因此不承诺跨用户或跨电脑直接恢复登录。

导入 settings.json 会先校验、自动备份当前文件，再替换配置并重启；缺失字段使用默认值。导入不改变数据保存路径，不携带账户或登录令牌。更换目录与导入均不结束已运行的 Minecraft，但重启后的启动器不会接管原游戏进程的输出。

## 验证记录

- 首轮完整回归：64 项中 63 项通过，设置定位用例失败；修复定位及更新旧视觉断言后，2 项 UI 回归通过。
- 新增数据与弹幕检查共 10 项通过：旧目录升级与 DPAPI 读取、Java 保留、自定义目录复制与游戏排除、冲突／取消保留原路径、配置导入备份、四种屏蔽预设、精确／正则过滤以及完整日志保留与日志恢复。
- UI 回归包括两个新设置入口、顶栏模式同步、样式保存、无效规则禁用、单页面缓存、虚拟列表及原有窗口／管理功能。
- 离屏预览位于 artifacts/ui/Danmaku-settings-light.png、Data-settings-light.png、Light-button-contrast.png。这是 WPF 离屏渲染和模拟数据检查，未重新进行真实桌面观感、显示器 DPI、真实账户登录或发布程序自动重启的人工验收。

## 发布与构建

自包含目录：bin/publish-data-danmaku。压缩包：bin/iKunLauncherNext-win-x64-data-danmaku.zip。解压完整目录后运行 iKunLauncherNext.exe，无需另装 .NET。程序目录需要可写权限；Java 与游戏文件单独识别或下载。源码沿用现有 net9.0-windows 项目，本次未升级目标框架。

```powershell
dotnet build src/Launcher.App/Launcher.App.csproj -c Release
dotnet test tests/Launcher.Tests/Launcher.Tests.csproj -c SmoothUI --no-restore -p:TestSdkVersion=17.8.0 -p:XunitVersion=2.5.3 -p:XunitRunnerVersion=2.5.3
dotnet publish src/Launcher.App/Launcher.App.csproj -c Release -r win-x64 --self-contained true --no-restore -p:RuntimeFrameworkVersion=9.0.3 -p:Optimize=true -p:DebugType=none -p:DebugSymbols=false -o bin/publish-data-danmaku
```
