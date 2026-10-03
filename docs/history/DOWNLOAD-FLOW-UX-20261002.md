# 下载步骤与确认页优化

- 底部步骤进度条由 4 DIP 加粗为 8 DIP，使用圆角填充和柔和增长／回退动画。沿用舒缓、性能、关闭策略及系统减少动画设置；连续切换从当前显示位置衔接。动画只更新填充裁剪，不修改绑定的真实数值，不逐帧触发布局。
- 原版、Forge、Fabric、NeoForge 改为纵向玻璃卡片，使用已有的草方块、铁砧、Fabric、NeoForge 图标；选中项有蓝色细边、轻背景和勾选标记。沿用 Welcome 入场与悬停效果。
- 游戏、模组、整合包确认页增加简短的字段名称，区分游戏名称、目标游戏、文件／模组版本、Minecraft、加载器与安装目录。长标题和路径换行；模组目录显示实际的 `mods` 目录。
- 删除模组确认页的“兼容性由你确认”。未增加模组兼容性限制。
- 游戏、模组、在线整合包、本地整合包和重试成功入队后，后台统一返回下载 Welcome 页，并打开任务列表。关闭面板后即可继续选择新内容。入队失败时保留原步骤和选择。
- 保留 Apple 蓝 `#007AFF`、统一操作按钮规格，以及已有的下载器、安装器与数据目录。

## 验证

- 应用构建通过，0 警告、0 错误。
- WPF 回归检查通过：页面缓存与唯一性、浅深主题、四加载器图标与选中态、标准尺寸下的版本选择框可见性、确认字段、三档进度动画、取消动画后的最终数值，以及本地 ZIP 提交到队列后返回 Welcome 页。
- 本地队列检查使用模拟整合包；未下载真实游戏／模组，未启动 Minecraft。本次未进行人工桌面操作或扩大 DPI 验证。
- 离屏结果：`artifacts/ui/Loader-cards-Light.png`、`Loader-cards-Dark.png`、`Mod-confirm-labeled.png`、`Pack-confirm-labeled.png`、`Download-home-after-queue.png`。

发布包：`bin/iKunLauncherNext-win-x64-20261002-download-flow.zip`。
源码包：`bin/iKunLauncherNext-source-20261002-download-flow.zip`。
