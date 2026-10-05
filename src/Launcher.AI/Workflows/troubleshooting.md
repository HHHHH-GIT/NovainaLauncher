# 故障分类与修复顺序

审校：2026-10-05。

1. **未授权或文件冲突**：让用户选择项目；hash 不匹配重新读取和合并，不强制覆盖。路径越界、链接、密钥拒绝不是可用另一条命令绕过的障碍。
2. **JDK**：javac 不存在是 JRE；UnsupportedClassVersion 或工具链错误先比较项目 Java/Gradle 兼容矩阵。Gradle 运行 Java 与编译 toolchain 是不同设置；选择正确 JDK，不乱改全局 JAVA_HOME。
3. **Wrapper/网络**：读取真实 distributionUrl、版本和 checksum。服务端超时、TLS、Maven 错误先定位对应来源。不能关闭 TLS/校验、替换成任意陌生镜像或反复清空全部缓存。首次同步依赖可能很慢，应展示真实输出，而非假百分比。
4. **依赖解析**：检查坐标、repository、Minecraft/加载器版本与插件组合；模板已有依赖版本应优先保留，不能伪造 Maven 坐标。
5. **源码/API**：按文件/行号处理第一项根因，确认映射、API 签名、返回类型、事件总线、source set；不能用删除功能或空 catch 掩盖。编译后继续处理剩余错误。
6. **资源/打包**：核对 mod 元数据、占位符展开、JSON、命名空间与生产 JAR。不能通过删掉验证任务、依赖声明或整个资源目录“修复”。

终端输出有界并脱敏；被截断时说明截断，不说已经读完整日志。任务取消会等待进程树清理，下一轮再核实。项目 build 脚本可执行任意代码，工作目录不是操作系统沙箱。

来源：
- Java/Gradle：https://docs.gradle.org/current/userguide/compatibility.html
- Toolchains：https://docs.gradle.org/current/userguide/toolchains.html
- Wrapper：https://docs.gradle.org/current/userguide/gradle_wrapper.html
- 安全：https://docs.gradle.org/current/userguide/best_practices_security.html
- 依赖验证：https://docs.gradle.org/current/userguide/dependency_verification.html
