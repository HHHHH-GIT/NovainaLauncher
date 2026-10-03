# 流体玻璃 UI 与 Java 下载优化（2026-10-01）

## 已实现

- 统一玻璃胶囊按钮、局部悬停高光、按压反馈、导航和菜单；内容区保留清晰分组。
- Windows 10 采用原生圆角窗口区域，随窗口尺寸及 DPI 更新；内容同步裁剪。Windows 11 使用原生 DWM 圆角。
- 动画档位：关闭／性能／舒缓。设置持久化，旧配置默认舒缓；页面、侧栏、菜单和定位共用策略，尊重系统动画设置。
- 设置整合为外观与动画、启动、Java、高级四组。侧栏子项和收起后的顶栏下拉共用定位状态，侧栏切换保留当前页面与滚动位置。
- Java 最多四个异步分段连接，Range 不受支持时使用单连接；显示百分比、实时速度及逐连接详情。
- 主动取消覆盖请求、下载、SHA-256 校验和逐条目解压；失败或取消清理本次临时文件。关闭启动器等待下载取消与清理，不结束已运行游戏。
- 下载失败的详细原因留在详情面板；完成安装才加入 Java 列表，并重新匹配当前版本。

## 验证

15 项测试全部通过。新增测试涵盖分段数据合并、速度与进度、无 Range 降级、未知文件长度、异常范围、连接失败、主动取消及临时文件清理、校验失败、解压取消。UI 回归涵盖单页面宿主、设置定位、动画档位持久化、侧栏切换保留位置和下载详情绑定。

离屏渲染覆盖五个页面，另生成深色设置页面的 125%／150%／200% DPI 图像与下载详情图像（artifacts/ui）。这不能代替多显示器 DPI、实际拖动或桌面材质验收。

桌面检查第一次捕获报 FrameArrived timed out；重新定位窗口后，用户通过物理 Escape 停止 Computer Use。实际窗口观感和 Windows 11 行为未标记为通过。

## 发布与构建

bin/publish-glass：Windows x64 自包含包，包含 .NET 与 WPF 运行时，启用编译优化。

```powershell
dotnet publish src/Launcher.App/Launcher.App.csproj -c Release -r win-x64 --self-contained true -o bin/publish-glass
```

本机测试使用已缓存的测试运行器，生产依赖没有降级：

```powershell
dotnet test tests/Launcher.Tests/Launcher.Tests.csproj -c Glass -p:TestSdkVersion=17.8.0 -p:XunitVersion=2.5.3 -p:XunitRunnerVersion=2.5.3 -p:NuGetAudit=false -p:RestoreSources=C:\Users\LBD06\.nuget\packages
```

设计参考：[Apple 按钮指南](https://developer.apple.com/design/human-interface-guidelines/buttons)、[Meet Liquid Glass](https://developer.apple.com/videos/play/wwdc2025/219/)。
