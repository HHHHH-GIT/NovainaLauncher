# 窗口轮廓、智能内存与版本内容管理

更新日期：2026-10-01。

## 本次变更

- Windows 10 的原生圆角区域按客户区坐标、内容偏移及 DPI 计算，与 WPF 内容裁剪一致。布局完成后更新，合并重复通知；处理窗口模板尚未挂接内容的初始化阶段。关闭旧的全窗口玻璃框架，Windows 11 去除额外 DWM 边框。Windows 11 常规模式使用原生圆角，整体透明模式使用窗口区域保持轮廓。
- 整体透明度 0–50%，默认 0%。包含文字与按钮，通过原生统一 Alpha 实现，保持 AllowsTransparency=False；拦截 WPF 清除系统层窗口标志的行为。非零透明度关闭系统亚克力，归零恢复。
- 默认智能内存；启动与每分钟后台读取可用物理内存，滑块上限动态更新。分配量为可用量 × (0.50 + 0.01 × min(启用 Mod 数,20))，向下取整至 256 MiB 并限制在可用范围内。手动拖动关闭智能，开关可恢复；启动前重新采样并固定请求，游戏运行期间不改变 JVM 内存。
- Java 下载目录可选择、重置，下载开始时固定目录；扫描包含默认与自定义 runtime。临时文件清理使用本次目标目录，不迁移旧 Java。
- 当前账户与版本使用强调背景、边框和“✓ 当前”。有效版本点击“设为当前”后跳到启动页。
- Mod、存档、资源管理作为版本管理子页面，返回保留版本列表位置。统一遵循共享、隔离与自动目录设置；只浏览不创建内容目录。
- Mod 支持多文件导入、启用/禁用、删除；存档支持文件夹/ZIP 导入、ZIP 导出和删除；资源包/光影包支持 ZIP/文件夹导入、移入/移出 .disabled 禁用目录、删除。资源操作不自动修改 options.txt。
- 删除确认后使用 Windows 回收站，不回退为永久删除。同名导入拒绝覆盖，存档 ZIP 安全解压至暂存目录后提交；长文件操作可取消。准备/运行中的游戏目录限制管理写操作。关闭启动器等待未完成的下载与内容任务取消。
- 管理列表与内存状态统一派发到 UI 线程，避免后台更新 CollectionView 异常。

## 验证记录

30 项自动化测试通过，包含既有核心、认证、日志、分段下载与 UI 回归，以及内存计算/配置保存、目录隔离、Mod 导入和状态切换、资源禁用、存档 ZIP 往返、导入冲突、非法路径/取消、自定义 Java 下载失败清理、真实临时文件回收站删除。

UI 测试使用独立 Application 资源，阻止启动生产界面或访问真实账户；检查单页面宿主、设置定位、当前项样式、选择后跳转、管理子页返回、后台列表更新和内存手动切换。

Windows 10 原生 HWND 检查验证圆角区域排除角点/保留中心，以及 30% 整体透明度的 Alpha 设置和归零恢复。这是接口级检查，不等于桌面外观验收。

离屏图像位于 artifacts/ui，包含浅色页面、深色设置的 125%/150%/200% 图像以及带存档条目的管理页面。发布程序实机初始化日志确认扫描 43 个本地版本、发现 6 个 Java，并到达“启动器就绪”。

Computer Use 桌面捕获失败：第一次 FrameArrived timed out，重新定位后 window capture timed out。因此实际拖动/缩放/恢复、多显示器 DPI、桌面亚克力和透明度观感、Windows 11 路径未标记通过；本次没有重新验收真实账号或启动真实游戏。

## 构建与发布

沿用现有 net9.0-windows 项目和固定依赖。发布包含 .NET/WPF 9.0.3 Windows x64 运行时，启用 Release 优化，无需安装 .NET。

```powershell
dotnet test tests/Launcher.Tests/Launcher.Tests.csproj -c ContentUpdate --no-restore -p:TestSdkVersion=17.8.0 -p:XunitVersion=2.5.3 -p:XunitRunnerVersion=2.5.3
dotnet publish src/Launcher.App/Launcher.App.csproj -c Release -r win-x64 --self-contained true --no-restore -p:RuntimeFrameworkVersion=9.0.3 -p:Optimize=true -p:DebugType=none -p:DebugSymbols=false -o bin/publish-management
```

本机使用缓存的测试工具版本，应用依赖未变更。首次在新环境构建先运行 dotnet restore；常规发布可省略 RuntimeFrameworkVersion 参数，采用 SDK 可用的运行时补丁版本。

发布目录：bin/publish-management。压缩包：bin/iKunLauncherNext-win-x64-management.zip。解压整个目录后运行 iKunLauncherNext.exe，Java 与游戏文件单独识别或下载。
