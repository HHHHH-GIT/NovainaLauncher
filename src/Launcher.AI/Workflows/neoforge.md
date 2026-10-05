# NeoForge：ModDevGradle 与版本化 API

审校：2026-10-05。NeoForge 1.21–1.21.1 使用 64 位 JDK 21。官方提供 ModDevGradle 和 NeoGradle MDK，现有项目使用哪一个就保留哪一个，不能只替换插件名。新项目从实际存在的 NeoForgeMDKs 对应版本仓库取得 commit 固定模板，不使用未经查询的版本 URL。

检查 mod_id、mod_version、minecraft_version、neo_version、plugin/toolchain、mappings 和 META-INF/neoforge.mods.toml。旧的 Forge META-INF/mods.toml 与 NeoForge 各代要求不同，应看实际 MDK；不要仅替换包名前缀来移植 Mod。主入口注解、模组总线、游戏总线、客户端注册分开。

使用当前版本 DeferredRegister/DeferredHolder 及相应的注册辅助方法；例如物品/方块不同的 convenience API 可能随版本调整，先查看模板或官方对应版本代码。动态注册、同步、数据驱动 registry 与静态 registry 不相同，不能靠静态 new 对象跳过注册流程。

网络使用对应版本的自定义 Payload、StreamCodec、注册事件及方向。确认处理在哪个线程，访问游戏状态时按文档调度；服务端校验请求。不直接复制 Forge 的旧式 SimpleChannel 或 Fabric 的类名。实体/菜单/持久数据等复杂功能先做最小构建里程碑，再增加同步与资源。

build 后读取实际 JAR 的 neoforge.mods.toml。专用服务端、数据生成、网络行为是否通过需要独立证据，不能由 exit code 0 推断。

官方来源：
- 环境/MDK：https://docs.neoforged.net/docs/1.21.1/gettingstarted/
- 元数据：https://docs.neoforged.net/docs/1.21.1/gettingstarted/modfiles/
- 注册：https://docs.neoforged.net/docs/1.21.1/concepts/registries/
- Payload：https://docs.neoforged.net/docs/1.21.1/networking/payload/
- 1.21.1 模板：https://github.com/NeoForgeMDKs/MDK-1.21.1-ModDevGradle
