# Novaina Launcher 宣传短片

65 秒，1920×1080，30 fps，H.264 / AAC MP4。画面以 Apple 发布会的留白、大标题、产品近景和节奏剪辑为参考，采用启动器自己的蓝紫像素星品牌。

## 影片结构

| 时间 | 内容 |
|---|---|
| 00:00–00:02.8 | 同款像素星凝聚、闪烁、爆炸；仅动画与原创音效 |
| 00:02.8–00:07.6 | Novaina 产品揭示；音乐开始 |
| 00:07.6–00:12.9 | 启动页、账户、版本、Java |
| 00:12.9–00:17.7 | 游戏、模组、整合包下载 |
| 00:17.7–00:23.4 | 运行日志弹幕 |
| 00:23.4–00:29.7 | 3D 皮肤与账户 |
| 00:29.7–00:51.8 | AI 指令、偏好提问、下载、配置、校验 |
| 00:51.8–00:56.6 | 整合包就绪与一句话导出 |
| 00:56.6–00:59.9 | 其他 Agent 能力 |
| 00:59.9–01:05 | 品牌收尾 |

## 说明

界面为 HTML / React 重现的产品流程演示，包含脚本驱动的时间压缩与进度，并非真实账户或联网安装录屏。3D 皮肤使用启动器同款 skinview3d 渲染 Steve。没有读取或使用账户、API Key、登录令牌或用户日志。

音乐使用用户提供的《Assumptions》音频，从影片 2.8 秒处开始，取歌曲开头约 62.2 秒并在结尾渐隐。超新星音效由 `make_sfx.py` 合成，为本片原创。工程压缩包不包含商业歌曲文件；重新渲染前，请自行放入有权使用的音频。

## 修改与渲染

需要 Node.js 22 或以上。

```powershell
npm.cmd ci
# 将音乐保存为 public/assumptions.mp3
npm.cmd run studio
# 或离线出片
npm.cmd run render
```

`src/Film.tsx` 管理分镜、帧号、文案、运镜、皮肤与音频时间；`src/style.css` 管理界面外观。所有动画依据帧号计算，不使用持续计时器或随机渲染。`render.mjs` 自动查找常见浏览器，缺失时由 Remotion 准备。也可通过 `NOVAINA_RENDER_BROWSER` 指定浏览器路径。

生成关键帧：`npm.cmd run stills`。只生成指定帧：设置环境变量 `NOVAINA_STILL_FRAMES=160,790,1300`。

## 依赖与素材

- [Remotion 4.0.532](https://www.remotion.dev/docs/renderer/render-media)：React 视频渲染；遵循 Remotion 许可证。
- React 19.2.0：MIT。
- [skinview3d 3.4.2](https://github.com/bs-community/skinview3d)：MIT；Three.js 由其依赖提供。
- 图标与默认 Steve 纹理：复用启动器现有素材，出处保留在项目第三方声明中。
- [Apple Events](https://www.apple.com/apple-events/)：视觉参考；未使用 Apple 标识、演讲影像或品牌素材。
- Sam Gellaitry《Assumptions》：用户提供，仅用于本片配乐。

成片输出：`output/Novaina-Launch-Film-1080p.mp4`。
