# 构建与发布

## 环境

- Windows 10 1809+ / Windows 11，x64；WPF 不支持在 Linux 上运行。
- .NET 9 SDK；`global.json` 指定 9.0.201，`latestFeature` 允许同主版本更新。
- Python 3.11+：发布脚本用标准库打包源码，无 Python 第三方依赖。
- Git；Node.js 22+ 只用于可选的 `promo` 宣传片。

```powershell
dotnet restore iKunLauncherNext.slnx
dotnet build iKunLauncherNext.slnx -c Release --no-restore
dotnet run --project src/Launcher.App/Launcher.App.csproj -c Release
dotnet test tests/Launcher.Tests/Launcher.Tests.csproj -c Release --no-build
```

开发运行默认游戏目录为项目根下 `.minecraft`。程序数据默认在可执行文件旁，真实账户不要作为测试数据。依赖版本固定在各项目文件中。

## 一键生成发布文件

```powershell
./scripts/Publish.ps1
```

从项目版本读取文件名，依次构建 `SingleFile` 和 `SingleFileLite` 配置：

| 输出 | 说明 |
| --- | --- |
| `bin/NovainaLauncher-<版本>-win-x64.exe` | 自包含、压缩的单文件完整版 |
| `bin/NovainaLauncher-<版本>-win-x64-lite.exe` | 单文件 Lite，需要 .NET 9 Windows Desktop Runtime x64 |
| `bin/NovainaLauncher-source-<版本>.zip` | 只包含源码、文档和必要素材 |
| `bin/SHA256SUMS.txt` | 上述三份文件的 SHA-256 |

临时构建位于 `artifacts/publish`。旧版本的可运行包从 bin 移出，历史源码 ZIP保留于 `bin/source-history`；若旧发布目录含 data，先备份再删除目录。

源码打包单独执行：`py scripts/package_source.py`。已有 EXE 时可用 `./scripts/Publish.ps1 -SkipBuild` 重打源码与校验清单。

脚本不改用户 data、runtime、游戏目录，不上传凭据。发布为非裁剪单文件，内嵌皮肤脚本、纹理与许可，使用时提取到 data/assets。WebView2 浏览器 Runtime、Java 和游戏文件不随 EXE 分发。

## 实际 EXE 的离线检查

```powershell
./bin/NovainaLauncher-1.0.0-win-x64.exe --verify-package
./bin/NovainaLauncher-1.0.0-win-x64-lite.exe --verify-package
```

该入口使用临时数据、禁止网络，检查内嵌资源/许可、Skia 解码、默认 Steve 头像与四个页面布局，不打开主窗口。它不是微软/LittleSkin 登录、真实下载、游戏启动或 IME/桌面交互验收。

## Git 与 GitHub

`bin`、`artifacts`、data、runtime、游戏、日志、商业配乐、node_modules 与测试输出由 `.gitignore` 排除。发布文件上传 Release，不放 Git 历史。

更新版本时同步项目版本与发布说明，先构建/校验，审查待提交文件，再建立 `v<版本>` 标签。源码 ZIP 包含 `LICENSE` 和第三方声明，不打包用户文件。
