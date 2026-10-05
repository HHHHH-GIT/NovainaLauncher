# 客户端、服务端与网络安全

审校：2026-10-05。物理客户端/专用服务端和逻辑客户端/逻辑服务端是两个维度。单机的同一 JVM 中仍有逻辑服务端；不能用“这是客户端进程”判断谁负责权威状态。伤害、奖励、库存和方块改变等由服务端决定，显示、声音、HUD 和按键由客户端负责。

公共类加载时不得直接引用客户端专用类。把 renderer、screen、key binding 和 Minecraft 客户端入口放到 client source set 或加载器规定的客户端注册路径。不要通过静态变量在逻辑侧之间共享数据；用加载器对应版本网络协议或已有同步能力。

包结构应显式、有限长，解码有大小限制；服务端重新验证玩家身份、交互距离、权限、界面状态和数量，不允许客户端指定任意文件/命令/收益。处理线程按 API 文档处理，不能从网络线程任意访问世界。避免一 tick 一包、大型全量同步与用户输入未经校验广播。

工作台默认不运行游戏。对于网络、实体、Mixin 或渲染功能，应明确：构建通过尚未验证运行时行为；交付提供一个人工测试清单，而非伪造“联机测试通过”。

来源：
- Forge 侧别：https://docs.minecraftforge.net/en/1.20.1/concepts/sides/
- Forge SimpleImpl：https://docs.minecraftforge.net/en/1.20.1/networking/simpleimpl/
- NeoForge Payload：https://docs.neoforged.net/docs/1.21.1/networking/payload/
