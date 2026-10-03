# 浅色玻璃、页面缓存与横向页签

2026-10-01。

## 本次调整

- 浅色按钮采用略带蓝灰的玻璃底色，边缘改为更清楚的半透明灰蓝轮廓。边缘在高光层上方绘制，防止亮色渐变覆盖边界。悬停显示蓝色轮廓、轻蓝底色与随指针移动的局部高光；深色模式保留白色高光。指针光效位置更新限制约每 16 ms 一次，避免高回报率鼠标产生重复绘制。
- 主页面按 ViewModel 缓存控件实例。窗口加载后在 UI 空闲时分批预创建六个页面；只有当前页面接入控件树，其他页面脱离布局与绘制。页面切换复用控件，保留各页状态，原有登录面板关闭／密码清理和日志订阅生命周期继续有效。
- 版本、账户、Mod／存档／资源列表改为可回收的虚拟列表，按像素滚动，只生成可见区域附近的行。版本扫描和内容刷新批量替换集合，避免逐项通知反复构建列表。
- 管理概览读取的四类内容在会话内复用，缓存最多 10 秒；切换页签优先使用已有数据。刷新、改名及文件写操作清理缓存，启用／禁用后不会继续显示旧状态。概览的目录读取并行在后台执行，纯浏览不再重复触发内存计算。
- 详情页选中胶囊平滑横向移动，宽度随页签调整；内容按切换方向进行轻微横向位移和淡入。连续切换从胶囊当前位置衔接；动画档位、系统减少动画与关闭档立即生效。仍然只有一个内容区域，不保留叠加的上一页面。
- 主题应用跳过值未变的资源，减少重复资源更新导致的界面重绘。

## 验证记录

54 项完整回归通过；最终边缘绘制调整后，2 项 UI 回归再次通过。包括：

- 多轮导航使用相同页面实例，六个缓存页面，控件树中始终只有一个活动页面。
- 2000 条版本数据的实际生成行数限制、滚动后的虚拟化以及账户选择回归。
- 内容页缓存复用，Mod 操作后缓存失效并显示正确启用状态。
- 浅色默认边界、实际悬停事件的蓝色轮廓与高光强度、白色背景上的高光像素差异。
- 在屏幕外的测试窗口中创建横向动画，检查活动动画、连续切换和关闭档归位。
- 原有名称／改名、账户、下载、内存、文件管理、窗口轮廓及页面不重叠回归。

离屏性能数据见 artifacts/ui/Navigation-performance.txt。测试使用 2000 条版本数据、40 次导航及布局更新；记录耗时与实际行数。这不包含桌面 GPU 帧率、鼠标手感或真实游戏运行负载，不将其作为桌面卡顿完全消除的验收。

浅色预览：artifacts/ui/Versions-light-cached.png。
按钮对比：artifacts/ui/Light-button-contrast.png。
测试没有修改真实游戏或真实账户，也没有重新进行桌面观感、显示器 DPI 或真实游戏启动验收。

## 发布

发布目录：bin/publish-smooth-ui。
自包含压缩包：bin/iKunLauncherNext-win-x64-smooth-ui.zip。

解压完整目录后运行 iKunLauncherNext.exe，无需安装 .NET；Java 和游戏文件仍单独识别或下载。包含前面已完成的名称修复、账户单击／双击、动画档位、管理页与下载功能。

```powershell
dotnet test tests/Launcher.Tests/Launcher.Tests.csproj -c SmoothUI --no-restore -p:TestSdkVersion=17.8.0 -p:XunitVersion=2.5.3 -p:XunitRunnerVersion=2.5.3

dotnet publish src/Launcher.App/Launcher.App.csproj -c Release -r win-x64 --self-contained true --no-restore -p:RuntimeFrameworkVersion=9.0.3 -p:Optimize=true -p:DebugType=none -p:DebugSymbols=false -o bin/publish-smooth-ui
```
