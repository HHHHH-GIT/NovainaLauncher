# 2026-10-04 开源整理

- 生成文档归入 docs；历史修复记录放 docs/history，开发原始计划归入 spec，补充当前架构规范。
- 保留最新完整版/Lite EXE和源码 ZIP；历史源码 ZIP归档 bin/source-history，移除 67 个旧构建/可运行包。
- 清理前从旧发布目录保留 42 处 data/runtime/路径引导等本地数据位置，备份在 artifacts/local-backups/bin-data-20261004；当前 bin/data 与路径引导不变。
- 新增 MIT LICENSE、AGENTS.md、新手 README、图文媒体说明和可复现发布脚本。
- 更新内嵌第三方声明位置，完整及 Lite EXE均嵌入项目 LICENSE；皮肤预览资源继续内嵌。
- 发布脚本的临时构建移至 artifacts/publish，源码包只包含公开文件；EXE作为 Release 附件，不入 Git 历史。
- 私有配置、DPAPI 凭据、日志、游戏、Java、商业配乐和宣传片成片不上传。宣传片源码保留，重新渲染时自行提供有权使用的配乐。

本次主要为目录、文档与发布整理，未更改游戏安装、登录或 Agent 业务。发布构建与实际包检查见 [1.0.0 发布记录](v1.0.0.md)；真实账号、网络安装与人工桌面交互不因整理而重新标记通过。
