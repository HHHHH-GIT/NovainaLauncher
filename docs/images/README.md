# README 媒体来源

| 文件 | 来源与展示边界 |
| --- | --- |
| `hero.jpg` | 原创品牌宣传片关键帧，只有 Novaina 图标与文案 |
| `ai.png` | 2026-10-06 宣传片采集的原生 WPF 基础模式；隔离的演示问题、选项与会话 |
| `ai-workbench-dark.png` | 同次原生 WPF 工作台采集；“星莓派”与构建输出为脚本演示，不能作为真实 Mod 构建验收 |
| `ai-settings-dark.png` | 同次原生 WPF 独立 AI 设置页采集，密钥为空 |
| `downloads.png` | 同次原生 WPF 下载 Welcome 页采集；演示缓存，不触发真实下载 |
| `skins.png` | 新宣传片第 23.67 秒；实际 WPF 账户页采集与同款 skinview3d 合成，演示账户和默认 Steve |
| `promo.jpg` | 新宣传片第 81 秒；原生 WPF 工作台与影片 JAR 交付文案合成，流程和产物为演示 |
| `startup.gif` | 实际 WPF 开场动画的桌面采样 |

品牌图标的生成来源保留在 [GENERATED-ICON.md](../../src/Launcher.App/Assets/Brand/GENERATED-ICON.md)。Steve、skinview3d 和其他第三方素材遵循 [第三方声明](../THIRD-PARTY-NOTICES.md)，不因根 LICENSE 变为 MIT。

本次采集由 [promo/capture](../../promo/capture/Program.cs) 使用临时数据目录完成，不读取真实账户、API Key、历史会话或游戏日志。详细制作方式见 [宣传片说明](../../promo/README.md)。

宣传片商业音乐与含该音乐的成片仅留在本机，不进入公开仓库或 Release。
