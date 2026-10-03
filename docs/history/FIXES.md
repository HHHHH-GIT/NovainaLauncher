# 界面修复记录（2026-10-01）

- 页面重叠：五个叠放页面改为单一内容宿主；修正绑定作用域。
- 状态绑定：统一处理布尔、计数、反向显示和按钮启用；空列表提示、取消按钮恢复正确状态。
- 动画：页面淡入位移、侧栏伸缩、按钮按压、账户面板淡入；遵循系统动画设置。
- 拖动：移除顶层 layered window 和大面积 DropShadow，使用 WindowChrome 原生移动与缩放。
- 材质：Windows 11 系统背景；Windows 10 composition acrylic；关闭透明效果时实色降级。Windows 10 拖动时暂停模糊，松开恢复。
- 控件：自定义滚动条、下拉框、滑块、提示框；系统文件选择器保留。
- 日志：批量更新、虚拟列表、预览数量上限；复制/导出读取完整日志文件。
- 弹幕：改用 RenderTransform，固定轨道，清理动画时钟，拖动与登录时暂停。
- 账户：LittleSkin 密码改为 PasswordBox；关闭面板取消请求并清理密码；微软旧请求不能更新新的登录面板。
- 版本与 Java：刷新后重新选择新目录中的版本对象；没有匹配 Java 时不再随意选择第一个。
- 设置：主题切换生效；内存滑块与 Client ID 输入延迟保存，避免每次输入同步写盘。

## 验证

7 项测试通过。UI 测试在 STA 线程加载真实 WPF 资源，依次布局并渲染五个页面，检查单页面宿主、自定义滚动条、状态转换及离开账户页后的密码清理。渲染输出：artifacts/ui。

本机 NuGet 下载不稳定，测试使用已缓存的测试运行器，生产依赖没有降级：

```powershell
dotnet test tests/Launcher.Tests/Launcher.Tests.csproj -c UiFix -p:TestSdkVersion=17.8.0 -p:XunitVersion=2.5.3 -p:XunitRunnerVersion=2.5.3 -p:NuGetAudit=false -p:RestoreSources=C:\Users\LBD06\.nuget\packages
```

桌面检查收到物理 Esc 后已停止，没有继续自动操作桌面。实际拖动帧率、系统材质、不同 DPI 和真实账户登录不属于本次已完成的实测结果。

背景适配参考：[WPF UI 4.3.0 WindowBackdrop 源码](https://github.com/lepoco/wpfui/blob/4.3.0/src/Wpf.Ui/Controls/Window/WindowBackdrop.cs)。

## 发布包

`bin/publish-fixed` 为 Windows x64 自包含输出，包含 .NET 9.0.3 和 WPF 运行时，无需单独安装 .NET。发布 DLL 与编译产物的 SHA-256 已核对一致。

继续检查时已启动发布包，进程保持响应。Computer Use 窗口捕获连续出现 `FrameArrived timed out` 与 `window capture timed out`，因此实际桌面观感仍未完成复核。
