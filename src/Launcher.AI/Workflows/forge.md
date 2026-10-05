# Forge：MDK 与版本对应

审校：2026-10-05。1.20.1 官方入门要求 64 位 JDK 17；不能由此推断所有 Forge 版本都是 Java 17。使用 list_mod_templates 从官方 Maven promotion 查询实际推荐或最新 MDK，返回 template_id 后再初始化。不要把 installer 或 universal JAR 当 MDK。旧版本的 ForgeGradle/Java/Gradle 组合必须保留并验证。

检查 gradle.properties、build.gradle、settings.gradle、gradle-wrapper.properties 和 META-INF/mods.toml。mod_id、@Mod、包名、mod 版本、Minecraft 与 Forge 依赖区间保持一致。采用官方 MDK 现有 Mojang 映射，不随意引入别的映射。先 build 原始模板，再实施功能；不为解决 API 错误把 Forge 升级到另一 Minecraft 主版本。

注册使用该版本的 DeferredRegister/RegistryObject 与正确的 Mod event bus。加载生命周期、注册事件、游戏事件并非同一个事件总线。将客户端 renderer/key mapping 注册隔离，公共方块/物品不得引用 net.minecraft.client。权威游戏逻辑在逻辑服务端执行，单机也包含服务端逻辑。

资源的 namespaces、模型、blockstates、语言、配方/掉落和 tags 与注册内容逐个核对。网络采用对应版本 SimpleChannel/SimpleImpl 的编码、解码、方向、协议版本及处理线程，不将 NeoForge 新式 Payload 文档直接套用。来自客户端的请求在服务端校验权限、位置、大小与频率，不能相信客户端传入的奖励或物品数量。

构建使用项目 Wrapper 的 build，检查实际 build/libs 产物和 mods.toml。缺失翻译/纹理等资源不一定导致编译错误；默认交付报告需单独说明资源静态检查与游戏内验证。

官方来源：
- MDK/JDK/构建：https://docs.minecraftforge.net/en/1.20.1/gettingstarted/
- 注册：https://docs.minecraftforge.net/en/1.20.1/concepts/registries/
- 侧别：https://docs.minecraftforge.net/en/1.20.1/concepts/sides/
- 网络：https://docs.minecraftforge.net/en/1.20.1/networking/simpleimpl/
- 配方：https://docs.minecraftforge.net/en/1.20.1/resources/server/recipes/
- MDK 官方索引：https://files.minecraftforge.net/
