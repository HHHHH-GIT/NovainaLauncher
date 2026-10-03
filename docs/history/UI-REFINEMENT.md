# 游戏名称、账户交互与动画调整

2026-10-01。本次修正上一版游戏名称被统一显示为 Minecraft 的问题。

- 游戏列表、管理页标题、名称输入和启动页显示真实实例名称。未通过启动器改名的游戏使用现有实例文件夹名称；已保存的名称继续使用。例：内战1111、1.20.1-Fabric 0.19.5、1.19-OptiFine_H9。
- 右侧小标签只显示加载器类型，避免重复名称。Minecraft 与加载器详细信息仍保留在管理页概览中。
- 账户整行单击设为当前，双击设为当前并返回启动页；空格选择、Enter 选择并返回。删除按钮独立处理，不触发行选择。移除重复的“设为当前”按钮，以蓝色行高亮和“✓ 当前”标记当前账户。
- 性能模式在上一版时长基础上缩短 25%，舒缓模式增加 15%。保留原有动画效果，关闭档与系统关闭动画行为保持一致。

| 动画 | 性能 | 舒缓 |
| --- | ---: | ---: |
| 页面、定位滚动 | 267.75 ms | 644 ms |
| 侧栏 | 229.5 ms | 552 ms |
| 按钮反馈 | 114.75 ms | 276 ms |
| 菜单、弹窗 | 153 ms | 368 ms |

## 验证

54 项自动化测试通过。使用临时目录检查截图中的实际名称、已有改名持久化、账户行鼠标事件选择与返回主页命令、当前项标记，以及全部动画时长；保留原有改名、文件管理、下载、窗口轮廓与页面宿主回归。离屏图像见 artifacts/ui/Version-rows.png 和 Account-selection.png。

本次只读取真实游戏目录的文件夹名称及 JSON id／inheritsFrom，确认现有实例的命名形式；未改动实际游戏目录、版本文件或真实账户。实际桌面双击及完整游戏启动未重新实测。

## 发布

解压 bin/iKunLauncherNext-win-x64-ui-refined.zip，运行 iKunLauncherNext.exe。发布目录 bin/publish-ui-refined，包含 Windows x64 .NET／WPF 运行时，无需另装 .NET。

```powershell
dotnet test tests/Launcher.Tests/Launcher.Tests.csproj -c VersionManager --no-restore -p:TestSdkVersion=17.8.0 -p:XunitVersion=2.5.3 -p:XunitRunnerVersion=2.5.3

dotnet publish src/Launcher.App/Launcher.App.csproj -c Release -r win-x64 --self-contained true --no-restore -p:RuntimeFrameworkVersion=9.0.3 -p:Optimize=true -p:DebugType=none -p:DebugSymbols=false -o bin/publish-ui-refined
```

