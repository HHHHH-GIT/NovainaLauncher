# 资源、数据与中文体验

审校：2026-10-05。先区分 assets/<modid>（客户端可见资源）与 data/<modid>（游戏数据）。目录与 JSON schema 随版本变化，不能把旧配方/模型目录直接复制到新版本。所有资源键使用稳定、全小写命名空间，资源路径不能包含中文、空格或玩家机器路径。

新增内容需成套检查：注册 ID → 翻译键 → zh_cn/en_us → item/block model → blockstates（若适用）→ 纹理引用 → recipe → loot → tags。语言文件是 JSON，转义与重复键检查；显示名支持中文，包名/mod id 不用中文。原版材质引用可用于最小原型，必须在报告中说明；不能声称生成了尚不存在的原创 PNG。

数据生成适合批量模型、配方、掉落、语言与标签，先查实际项目的 datagen 插件、入口、providers、sourceSets。runData 是执行代码，仍走构建信任确认。生成输出要加入正确 source set，生成后 build；避免手写与生成版本相互覆盖。已有玩家数据不删除，不覆盖 Minecraft 命名空间整份掉落表来实现简单增量。

复杂资源交付前检查 JSON 可解析、资源引用存在、同名 ID 无冲突、metadata 依赖声明完整。只能证明这些静态检查，渲染、声音与数据加载行为还需游戏运行验证。

来源：
- Fabric datagen：https://docs.fabricmc.net/1.21.1/develop/data-generation/setup
- Fabric 项目结构：https://docs.fabricmc.net/1.21.1/develop/getting-started/project-structure
- Forge 配方：https://docs.minecraftforge.net/en/1.20.1/resources/server/recipes/
