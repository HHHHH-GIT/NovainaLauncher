# Windows 10 圆角、动画与滑块修复

2026-10-01。此包包含上一版智能内存、Java 下载目录及版本内容管理功能。

## 窗口修复方式

Windows 10 改为独立原生框架：移除 WPF WindowChrome，让 WM_NCCALCSIZE 提供覆盖整个窗口的客户区；禁用 DWM 原生非客户区绘制，取消原生标题栏和窗口边缘样式。保留系统缩放框架及原生移动/缩放循环，通过自定义命中测试区分八个缩放方向、标题栏拖动和标题栏按钮。

原生圆角区域只由 WindowOutline 维护，并与内容的 20 DIP 圆角一致。GDI 区域输入保留末行/末列所需的一像素补偿；缓存尺寸相同时仍核对实际窗口区域，避免系统或其他代码改变区域后缓存阻止恢复。保持 AllowsTransparency=False 与原生整体透明度。

之前的 WindowChrome 会自行设置/清除窗口区域，且使用不同的圆角参数，与 WindowOutline 存在所有权冲突。[WPF WindowChromeWorker 源码](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Shell/WindowChromeWorker.cs)中的 _SetRoundingRegion、_ClearRoundingRegion 和 _HandleWindowPosChanged 可见该行为。此次消除 Windows 10 上的重复所有者。Windows 11 保留原有 DWM 路径。

## 动画与滑块

用户的“提速 10–2%”按约 10–20% 理解，选取时长缩短 15%。性能与舒缓使用相同效果，包括页面淡入/位移、侧栏伸缩、菜单/弹窗淡入、按钮反馈与设置定位滚动。

| 动画 | 性能 | 舒缓 |
| --- | ---: | ---: |
| 页面、定位滚动 | 357 ms | 560 ms |
| 侧栏 | 306 ms | 480 ms |
| 按钮微交互 | 153 ms | 240 ms |
| 菜单、弹窗 | 204 ms | 320 ms |

关闭档和系统关闭动画仍直接完成。性能档不再移除页面位移、侧栏或滚动效果。

动画档位滑块的蓝色填充作为独立底层，延伸至滑块中心下方，在最大值时覆盖完整胶囊。外轮廓统一裁剪，消除蓝色填充与白色圆形滑块之间的空白。保留拖动、点击、键盘操作及三档吸附。

## 验证

- 42 项测试通过，涵盖既有管理/下载回归和新增八方向缩放命中、标题栏按钮命中、滑块填充及动画时长检查。
- Windows 10 原生 HWND 检查：客户区尺寸与完整窗口一致，无原生标题栏；四角不属于区域，中心属于区域，区域覆盖末行/末列。
- 新增实际创建并显示于屏幕外的窗口探针，经过移动、缩放及最小化恢复后重复检查完整客户区和四角裁剪。该探针不接触真实账户或游戏文件。
- 滑块在中间档和最大档均进行了像素断言，确认原空白位置为蓝色填充；图像位于 artifacts/ui/Animation-slider-fixed.png。
- 离屏检查包含浅深主题及 125%/150%/200% 图像；不将离屏图像或屏幕外窗口探针视为桌面观感、多显示器 DPI 的完整验收。
- 已启动新发布程序进行桌面检查，用户通过物理 Escape 停止 Computer Use，随即停止全部界面自动操作。桌面最终观感未标记通过。

## 发布

Windows x64 自包含目录：bin/publish-rounded。压缩包：bin/iKunLauncherNext-win-x64-rounded.zip。包含 .NET/WPF 9.0.3，Release 优化，解压后运行 iKunLauncherNext.exe。

```powershell
dotnet test tests/Launcher.Tests/Launcher.Tests.csproj -c RoundedFix --no-restore -p:TestSdkVersion=17.8.0 -p:XunitVersion=2.5.3 -p:XunitRunnerVersion=2.5.3
dotnet publish src/Launcher.App/Launcher.App.csproj -c Release -r win-x64 --self-contained true --no-restore -p:RuntimeFrameworkVersion=9.0.3 -p:Optimize=true -p:DebugType=none -p:DebugSymbols=false -o bin/publish-rounded
```
