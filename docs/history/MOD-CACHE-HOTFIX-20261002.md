# 模组文件缓存与版本标签修复 · 2026-10-02

- 目标游戏标签读取实际 Minecraft 版本，优先使用 `clientVersion` 等显式元数据与 `--fml.mcVersion`，再解析有效的配置 / 继承版本 ID。实例名称保持在左侧；无法确定版本时显示“版本未知”，不拿游戏名替代。Forge 参数同时可补充加载器版本。
- 模组文件列表增加解析后的磁盘缓存与内存索引，保存在数据目录的 `downloads/mod-versions`。15 分钟内直接复用；过期缓存先展示，随后异步刷新，刷新期间仍可选择文件与安装，保留当前筛选及文件选择。刷新失败保留缓存。首次获取继续显示旋转加载标记。
- Modrinth 列表请求加入 `include_changelog=false`；元数据请求使用 gzip，缩短连接等待，设置整体超时。二进制下载器保持原有传输方式。参考：[Modrinth 文件版本接口](https://docs.modrinth.com/api/operations/getprojectversions/)。
- CurseForge 在取得总数后每批最多四路并行获取分页，保持服务端顺序与 ID 去重；无总数的响应仍逐页读取。
- 快速切换模组时取消旧请求，关闭或迁移数据时等待相关请求和缓存写入结束。磁盘缓存原子替换，内存仅保留最多 24 个索引。

## 本轮验证

- 构建：零警告、零错误。
- 19 项相关检查通过（约 4 秒），覆盖本次新增缓存 / 版本识别 / 分页、已有版本改名和 UI 回归。UI 检查使用离屏窗口，未标记为人工桌面验证；没有重跑游戏安装矩阵。
- 只读解析实际 `G:\MC\.minecraft\versions\临时内战包`：标签 `Forge · 1.20.1`。
- 实际获取 Entity Culling 文件列表：MCIM 和官方均返回 793 项。本次冷请求分别约 6256 / 1411 毫秒，说明镜像响应本身也会影响首次加载。网络耗时仅为这次测量，不能视为固定指标。
- 官方 JSON 去掉更新日志前 / 后约 1,397,474 / 677,704 字节，减少约 51.5%。解析后磁盘缓存读取约 18.8 毫秒；内存索引读取约 0.01 毫秒。记录位于 `artifacts/mod-cache-smoke/result.json`，测试数据独立于用户数据。

## 交付

- Windows x64 自包含包：`bin/iKunLauncherNext-win-x64-20261002-mod-cache.zip`。
- 源码包：`bin/iKunLauncherNext-source-20261002-mod-cache.zip`。
- 正在运行的旧启动器未停止。更新时保留现有 `data` 和数据路径引导文件、Java 与 Minecraft 文件。
