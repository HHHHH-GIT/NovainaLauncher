# 整合包、账户与侧栏更新（2026-10-02）

## 界面与操作

- 侧栏内固定宽度改为 174 DIP，扣除边框和左右留白后仍保留绘制空间；导航按钮与收起按钮的右圆角完整。
- 版本 → 管理 → 概览新增“删除此游戏”和“导出整合包”。删除需确认，移入回收站；被其他版本继承或引用客户端 JAR 的版本阻止删除。共享 Minecraft 文件和其他实例保留。
- LittleSkin 账户隐藏导入、经典／纤细选择，保留预览、刷新、导出与皮肤站入口，显示橙色提示“换皮肤请到在线网站，换完后刷新。”。微软和离线账户继续使用原来的皮肤操作。
- Mod 结果使用本地名称索引显示“中文名(英文名)”，例如“钠(Sodium)”。未知中文名显示原名，描述保持来源原文。英文原名仍用于搜索与文件请求；社区名称表载入后同步更新列表和已选标题。
- 下载欢迎页增加第三个长卡片“整合包”，与游戏、模组共用进入和步骤动画；整合包提供发现 → 版本与名称 → 确认安装。

## 整合包

- 接入 Modrinth / CurseForge 搜索、文件版本、图标和来源设置；版本列表沿用磁盘缓存、异步更新和加载圆环。
- 支持 Modrinth `.mrpack` 和 CurseForge manifest ZIP；Minecraft、Forge、Fabric、NeoForge 按清单指定的确切版本安装。Quilt 等当前核心没有接入的加载器在准备阶段给出原因。
- 游戏及加载器继续使用 CmlLib.Core 4.0.6 与原生安装器，文件传输继续使用 Downloader 5.9.8；整合包层负责标准清单与目录适配。
- CurseForge 文件信息优先按 100 个一批请求；缺失项或不支持批量的来源采用最多四路补查。未开放下载的文件明确报错，官方回退仍需要配置 API Key。
- 整合包文件校验 SHA 与声明大小，客户端不支持的文件跳过；逐条目应用 overrides，再应用 client-overrides。路径检查拒绝越界、驱动器路径、链接及覆盖实例配置。
- 游戏及内容均在任务暂存区准备，原生核心完整性检查和最终本地解析通过后提交命名实例。基础版本保留为内部依赖，列表只显示命名实例。取消等待文件传输收尾，任务暂存区清理后返回；已校验缓存独立保留以供重试。
- 下载 → 整合包提供本地导入，用于导入 `.mrpack` 或 CurseForge ZIP；文件名作为新游戏名称，同名拒绝覆盖。

## 本地导出

- 在概览选择保存路径，生成标准 `.mrpack`，保存 Minecraft 与加载器版本。
- 离线打包 `mods`、`config`、`defaultconfigs`、`resourcepacks`、`shaderpacks`、`kubejs`、`scripts`、`patchouli_books`、`datapacks` 及游戏选项文件，作为 overrides；禁用文件的目录和文件名保持原状。
- 游戏客户端、共享库、资源对象、Java、账户、启动器日志和存档不进入整合包。导出支持取消，成功后才替换目标文件。
- 独立 OptiFine 的版本使用 `ikun.extra.json` 扩展保留，iKun 导入时按该版本重建；第三方启动器是否识别该扩展未验证。Forge 中作为 Mod 的 OptiFine 随 mods 打包。

## 验证记录

- Release Windows x64 自包含发布成功；针对性自动检查 13 项通过，涵盖导出回读、整合包暂存与单实例提交、CF 批量解析、越界拒绝、依赖阻止删除、中文名称、来源 Key 隔离、下载取消及核心完整性补全。
- WPF 离屏检查通过：浅深主题、三个欢迎入口、游戏／模组／整合包各步骤只显示一个区域、LittleSkin 控件隐藏。截图在 `artifacts/ui/Welcome-Light.png`、`Welcome-Dark.png`；此结果不等同于人工桌面检查。
- 真实来源：搜索 Fabulously Optimized 得到 30 个结果，读取 473 个文件版本，下载并校验一个 `.mrpack`，解析出 Minecraft 1.21 / Fabric 0.15.11 / 48 个客户端文件，成功解压 overrides。记录在 `artifacts/modpack-smoke/result.json`。
- 独立目录实际导出并回装本地 Minecraft 1.20.1 / Fabric 0.19.5 实例，原生完整性检查通过，列表仅显示一个实例。记录在 `artifacts/modpack-smoke/roundtrip-result.json`。
- 实际取消回装检查通过：没有创建可见实例，本次暂存目录清除，校验缓存保留。记录在 `artifacts/modpack-smoke/cancel-result.json`。
- 未实测：本次游戏进程启动、CurseForge 全包实际安装、人工桌面鼠标操作和多 DPI。未将这些项目标为通过。

## 来源

- [Modrinth 整合包格式](https://support.modrinth.com/en/articles/8802351-modrinth-modpack-format-mrpack)
- [Modrinth 搜索与版本 API](https://docs.modrinth.com/api/)
- [CurseForge 文件与批量文件 API](https://docs.curseforge.com/rest-api/)
- [HMCL 社区模组名称数据](https://github.com/HMCL-dev/HMCL/blob/main/HMCL/src/main/resources/assets/mod_data.txt)

账户、数据位置、Java、已安装游戏保持既有设置。发布包不包含测试下载的游戏或 Mod 文件。
