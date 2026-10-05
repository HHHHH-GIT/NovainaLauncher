# Fabric：识别版本、映射与入口

审校：2026-10-05。示例资料按 1.21.1 组织；其他版本先读对应项目/官方分支，不迁移最新代码片段。官方文档的版本化示例可能使用 Mojang 映射，官方 example-mod 的具体分支也可能使用 Yarn。以 build.gradle 中 mappings 配置为准，不能看到“Fabric”就认定是 Yarn。

Fabric Loader、Fabric API、Loom 各司其职：运行加载、事件/接口、开发构建。先查询 FabricMC/fabric-example-mod 实际分支，拿到该分支 commit 后初始化，不能把 master 当作任意 Minecraft 版本。模板生成器也支持版本和映射选择，既有项目应保留原选择。

核对 gradle.properties 中 minecraft_version、loader_version、fabric_version、mod_version、maven_group，build.gradle 的 Loom 和 repositories。不要把其他加载器坐标放进这个项目。fabric.mod.json 的 id、版本、entrypoints、environment、depends 需要与真实类、Loader、Minecraft、Java 和 Fabric API 一致；保留 license。client 入口与 common 入口隔离，分离 source sets 时不得从 main 引用 client。

新增物品/方块时先核对该版本的 Registry、注册时机、标识符及属性构造器，资源与注册 ID 一致，创意标签使用当前版本 API。优先事件回调；只有缺少 hook 才添加 Mixin 并核对目标类、方法描述符和侧别。Mixin 能编译不代表注入目标存在，必须标明运行验证未完成。

数据生成要检查 Loom 的 fabricApi.configureDataGeneration 或现有配置、datagen entrypoint 和 generated resources 的加入方式。不要把某个版本的 client datagen 配置复制到所有版本。生成资源后再构建，语言、模型、配方、标签和掉落文件应由同一命名空间维护。

交付 build/libs 中生产 remap JAR；不要将 sources、dev 或 javadoc 包当作成品。初始 JDK 要与项目 toolchain 和 Wrapper 相容。

官方资料（按目标版本核对）：
- 创建项目/映射：https://docs.fabricmc.net/1.21.1/develop/getting-started/creating-a-project
- 目录/入口：https://docs.fabricmc.net/1.21.1/develop/getting-started/project-structure
- 物品：https://docs.fabricmc.net/1.21.1/develop/items/first-item
- 事件：https://docs.fabricmc.net/1.21.1/develop/events
- 数据生成：https://docs.fabricmc.net/1.21.1/develop/data-generation/setup
- 构建：https://docs.fabricmc.net/develop/getting-started/building-a-mod
- 官方模板：https://github.com/FabricMC/fabric-example-mod
