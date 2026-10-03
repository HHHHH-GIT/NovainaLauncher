# 游戏列表与统一管理页

2026-10-01。本次沿用既有 WPF、玻璃控件、动画策略和 Windows 10 原生窗口框架。

## 交互

- 版本列表压缩成约 58 DIP 单行：加载器图标、游戏名称、加载器标签、一个“管理”按钮。
- 原版使用草方块；Forge、NeoForge、Fabric、Quilt、OptiFine 使用各自项目图标，全部内嵌，界面无需联网取图。未知类型使用草方块兜底。
- 游戏名称显示实际实例文件夹名，例如“内战1111”或“1.20.1-Fabric 0.19.5”；不再将其统一替换为 Minecraft。加载器标签只显示类型。改名后名称与实例文件夹一致，启动页也显示游戏名称。
- 单击选择并保留列表，双击选择并跳到启动页；键盘空格选择、Enter 选择并跳转。“管理”不改变当前启动版本。
- 当前项保留蓝色背景、强调边框，新增图标角上的勾选标记。返回列表保留滚动位置。
- 统一管理页包含“概览、Mod、存档、资源包、光影包”页签。概览显示游戏名称、Minecraft 版本、加载器版本、Java 要求、版本类型、继承版本、版本／游戏目录、内容数量及共享／独立状态。既有导入、启用、禁用、回收站删除、存档导出、取消和目录使用锁仍有效。

## 改名

“保存名称”将 `.minecraft/versions/<名称>` 文件夹与同名 JSON／自有 JAR 改名，更新 JSON 的 id 以及其他本地版本的 inheritsFrom／jar 引用。真实 Minecraft 版本写入实例内的 `.ikun-profile.json`，保留旧版本 Java 需求识别；显示名称不参与加载器检测。

当前选择、每版本 Java／隔离设置随改名迁移，完成后重新扫描；管理中的游戏无需被设为当前。共享模式下不会重命名整个 `.minecraft`，共享存档和资源保持原目录。目录内其他文件随实例目录移动。游戏准备或运行时不允许改名，同名目录拒绝覆盖，文件提交失败回滚。短暂提交过程中不接受取消，防止一半完成的改名；关闭应用仍等待操作结束。

## 验证

51 项自动化测试通过。测试使用临时游戏目录与本地测试数据，不改动真实游戏或账户：

- 中文和空格名称、父版本和 JAR 引用更新、配置迁移、存档保留，CmlLib 解析及启动命令中的新 JAR 路径。
- 同名冲突、取消、无效名称、文件占用导致的回滚；游戏名称不能误改变加载器类型。
- 单击／双击命令、单行高度、仅一个管理按钮、当前项高亮、全部图标实际加载、统一管理页页签、改名后的所选项和目录、返回及单页面宿主。
- 原有窗口圆角、动画／滑块、Java 下载和文件管理回归。

离屏渲染截图位于 `artifacts/ui/Version-rows.png`、`Version-overview.png`、`Content-management.png`。此轮没有重新进行真实桌面鼠标双击、真实游戏启动、账号登录或多显示器 DPI 验收；离屏截图与启动命令测试不代表这些项目通过。

## 构建

```powershell
dotnet test tests/Launcher.Tests/Launcher.Tests.csproj -c VersionManager --no-restore -p:TestSdkVersion=17.8.0 -p:XunitVersion=2.5.3 -p:XunitRunnerVersion=2.5.3

dotnet publish src/Launcher.App/Launcher.App.csproj -c Release -r win-x64 --self-contained true --no-restore -p:RuntimeFrameworkVersion=9.0.3 -p:Optimize=true -p:DebugType=none -p:DebugSymbols=false -o bin/publish-versions
```

发布包：`bin/iKunLauncherNext-win-x64-versions.zip`。解压完整目录后运行 `iKunLauncherNext.exe`。包含 Windows x64 .NET／WPF 运行时，不包含 Java 或游戏。图标来源与声明见 `docs/LOADER-ICONS.md`，随包提供。


