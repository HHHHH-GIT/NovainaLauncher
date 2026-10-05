# 有证据的验证

审校：2026-10-05。基线构建用于区分环境与改动问题，最终 build/check 用于验证当前源码。不要沿用旧 JAR 的时间戳或上一次 exit code 当作本次成功。读取 build/libs 生产产物、hash 与 metadata；sources/dev/javadoc 文件不能交付为可用 Mod。

对于纯算法、配方计算、配置边界等可离线验证逻辑，优先利用既有 Java 测试配置，执行 test/check。需要注册/世界/加载器生命周期的行为，可在项目支持时使用 GameTest，但先检查对应版本 API 与任务，不能添加会编译却完全不运行的测试来“通过验收”。

静态检查覆盖 JSON、资源引用、翻译、元数据、依赖区间、入口、客户端隔离。运行检查覆盖 Mod 加载、主菜单、世界内行为、HUD/Mixin、存档重载、专用服务端和多人同步。这两类不可互相替代。

默认仅构建，未经明确请求不启动游戏；runClient/runServer 不是默认允许的终端 action。若玩家请求游戏内验证，通过基础业务准备准确测试实例，保留人工登录/EULA；不能测试时说明限制。

来源：
- Fabric 自动测试：https://docs.fabricmc.net/1.21.1/develop/automatic-testing
- Forge 构建/运行：https://docs.minecraftforge.net/en/1.20.1/gettingstarted/
- NeoForge 构建/服务端：https://docs.neoforged.net/docs/1.21.1/gettingstarted/
