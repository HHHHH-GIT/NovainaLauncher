# Novaina Launcher 宣传短片

90 秒，1920×1080，30 fps，H.264 / AAC 立体声 MP4。延续 Apple WWDC 风格的大标题、留白、产品近景与透视运镜，以及 Novaina 自己的蓝紫像素星品牌。保留原版超新星开场、原创音效与《Assumptions》配乐。

## 影片结构

| 时间 | 内容 |
|---|---|
| 00:00–00:02.8 | 像素星凝聚、闪烁、爆炸；仅开场动画与原创音效 |
| 00:02.8–00:07 | Novaina 品牌揭示；音乐开始 |
| 00:07–00:14 | 游戏、模组、整合包下载与加载器选择 |
| 00:14–00:21 | 游戏管理、实例概览、Mod 与导入导出 |
| 00:21–00:27 | 账户与 3D 皮肤预览 |
| 00:27–00:33 | 弹幕日志 |
| 00:33–00:36 | AI Mode 揭示 |
| 00:36–00:43 | 基础模式：一句话制作整合包 |
| 00:43–00:49 | 集中提问、选择偏好与回答折叠 |
| 00:49–00:56 | 工具调用归组、真实控件中的进度与结果详情 |
| 00:56–01:00 | 上下文分类用量、压缩、目标与引用 |
| 01:00–01:05 | 工作台与独立会话列表 |
| 01:05–01:11 | 从创意到 Mod 开发计划 |
| 01:11–01:19 | 编辑源码、资源与配方；Gradle 构建、结果展开与折叠 |
| 01:19–01:24 | JAR、资源及验证清单交付 |
| 01:24–01:30 | 品牌与开源仓库收尾 |

## 界面还原方式

界面窗口、顶栏、按钮、会话侧栏、Markdown、问题与工具控件来自当前启动器的 **原生 WPF 离屏采集**，以 1080×700 DIP、2 倍像素保存。不是重新拼造的 HTML 控件。Remotion 仅负责分镜、文案、透视、缩放、转场与合成。上下文弹窗也采集实际 WPF 控件。

3D 皮肤使用启动器同款 skinview3d 和默认 Steve 纹理，在实际皮肤控件所在区域合成。弹幕采用当前启动器的胶囊样式，按帧移动。

流程使用隔离的脚本演示数据：Nova、虚构实例及“星莓派 Mod”。时间与状态经剪辑压缩；片中安装、Gradle 输出和 JAR 是**产品流程示例**，不是本次实际 API、安装、构建或游戏测试的录屏。编译检查和游戏内体验验证分别表达。未读取真实 API Key、账户令牌、对话或游戏日志。

## 配乐与素材

音乐沿用用户提供的 Sam Gellaitry《Assumptions》，从影片 **2.8 秒**开始，使用歌曲前 87.2 秒，结尾 2.5 秒渐隐。片头 `intro-sfx.wav` 由原版 `make_sfx.py` 合成并保留。工程 ZIP 和 Git 均排除商业音乐；重新渲染需要自行提供有权使用的音频 `public/assumptions.mp3`。

- Remotion 4.0.532：遵循 Remotion 许可证。
- React 19.2.0、skinview3d 3.4.2：MIT；Three.js 为依赖。
- Novaina 图标、Minecraft Steve、加载器标志：保留 `licenses` 和启动器第三方许可声明。
- WWDC 仅为视觉风格参考，未使用 Apple 标识、演讲影像或品牌素材。

## 修改与渲染

需要 Node.js 22 或以上。工程 ZIP 已含原生界面 PNG，可直接重新渲染，无需运行 WPF 采集。

```powershell
npm.cmd ci
# 将有权使用的音乐保存为 public/assumptions.mp3
npm.cmd run studio
npm.cmd run stills
npm.cmd run render
node finish.mjs
python contact_sheet.py --verified
python package_source.py
```

- `src/Film.tsx`：2700 帧时间线、开场、背景、皮肤和音频。
- `src/ProductScenes.tsx`：原生界面素材、镜头与产品文案。
- `src/style.css`：影片强调元素；启动器控件已在 PNG 中保留原生样式。
- `public/native`：界面素材、皮肤区域坐标、采集清单。
- `render.mjs`：自动查找浏览器；可用 `NOVAINA_RENDER_BROWSER` 指定 Chrome/Edge。
- `finish.mjs`：验证 90 秒、2700 帧、1080p、30 fps、H.264/AAC、全片解码；采样关键帧，检查音效/音乐入点和结尾淡出，验证后复制到 `../bin`。
- `contact_sheet.py`：根据本次渲染清单生成分镜表；`--verified` 使用最终 MP4 解码出的帧，避免混入旧片素材。需要 Pillow。

只生成指定帧可设置 `NOVAINA_STILL_FRAMES=285,710,1350,2290`。所有动画根据帧号计算，没有持续计时器或随机渲染。

### 更新原生素材

在完整启动器仓库根目录使用 .NET 9 SDK：

```powershell
dotnet run --project promo/capture/Capture.csproj -c Release -- promo/public/native
```

采集器仅载入样式资源并创建隐藏的窗口句柄，不运行正常应用启动逻辑。数据使用唯一临时目录；下载组件使用演示缓存，不提交安装/启动操作。图片中的临时目录仅在显示层替换为 `C:\Novaina`，不改变文件访问位置。采集结束清理自己的临时数据。工程 ZIP 中保留采集源码供参考，但重新采集需要完整仓库中的 WPF 项目。

输出：`output/Novaina-Launch-Film-1080p.mp4`。
交付：`../bin/Novaina-Promo-1.0.0-1080p.mp4`、`../bin/Novaina-Promo-Remotion.zip`。
