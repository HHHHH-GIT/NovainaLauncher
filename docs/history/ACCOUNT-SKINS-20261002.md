# 账户皮肤整合

账户页采用并排账户与皮肤卡片，内容宽度不足 680 DIP 时上下排列。添加账户使用下拉菜单；单击选中、双击返回启动页。账户与首页头像显示皮肤头部及帽子层，像素保持清晰；未配置时使用 Steve。

## 功能

- skinview3d 3.4.2 + WebView2 1.0.4258.31 Composition 控件，本地脚本、透明背景、拖动旋转、滚轮缩放、经典/纤细模型、重置视角。
- 微软：按 UUID 读取公开皮肤；导入后“上传到 Minecraft”复用现有会话，成功后查询认证档案并更新头像。令牌仅用于 C# 对 Minecraft 服务的请求。
- LittleSkin：按角色 UUID 读取 Yggdrasil textures。导入可先预览并导出；在官方皮肤站应用后刷新。没有增加 OAuth 授权或皮肤站密码保存。
- 离线：导入后“使用此皮肤”保存本地头像及预览；不注入游戏内皮肤。
- 64×64 或 64×32 PNG；尺寸、解码、文件大小先检查；旧版强制经典模型。
- `data/skins` 包含相对路径元数据、纹理、头像和浏览器预览缓存；随原有数据目录迁移。
- 缓存先显示，15 分钟后后台刷新；强制刷新绕过有效期。同账户请求合并、最多两路网络，切换取消过期操作，网络失败保留已有皮肤。
- 角色选择框使用 Name 项模板，共用 ComboBox 补齐 ItemTemplateSelector，ToString 返回角色名，不再显示记录调试字符串。
- 关闭动画时模型静止，直接操作仍可用；隐藏、最小化及登录面板打开时暂停渲染。迁移与关闭等待皮肤写入及浏览器退出。

## 验证记录

- 编译通过：0 警告、0 错误。
- 5 项集中自动检查通过：PNG/帽子层/旧版，公开与认证档案解析，离线应用重启恢复，合并/取消/损坏缓存重取，以及原有页面缓存/选择/不重叠回归。角色下拉框选中项正确显示 YouriKunDad。
- Windows 10 实例化了真正的 Composition 控件、系统 WebView2 Runtime 154.0.4258.48 和 skinview3d WebGL 画布。程序内检查确认关闭动画静止，性能与舒缓运行，登录面板、隐藏页面与最小化均暂停。结果见 `artifacts/skin-desktop/result.json`。
- 实际初始化暴露了 Windows SDK 投影缺失，已将 WPF 目标设为 `net9.0-windows10.0.17763.0` 并固定 targeting pack 10.0.17763.57；恢复后初始化通过。
- 用户以 Esc 停止了桌面控制；桌面截图捕获未完成。两种主题、导航、透明度和实际鼠标旋转的人工视觉验收不标记通过。WPF 离屏布局回归单独记录，不能替代桌面验收。
- 未执行真实账号皮肤上传或 LittleSkin 网页更换；需要用户完成登录并选定实际皮肤后验收，不以模拟结果代替。

## 构建与发布

保留当前 .NET 9 SDK 与 WPF 工程，未更改系统 SDK 或用户数据。Windows 10 1809 及以上；3D 需要系统 WebView2 Evergreen Runtime，缺失时预览区域提供官方安装入口，头像与账户功能独立工作。

```powershell
dotnet build src/Launcher.App/Launcher.App.csproj -c Release
dotnet publish src/Launcher.App/Launcher.App.csproj -c Release -r win-x64 --self-contained true -p:Optimize=true -p:DebugType=none -p:DebugSymbols=false -o bin/publish-20261002-account-skins
py artifacts/package_welcome.py 20261002-account-skins
```

源码含本地 viewer 资源与第三方许可，无需 npm 才能构建。发布包不包含原有账户、游戏、Java 或开发检查的数据目录。
