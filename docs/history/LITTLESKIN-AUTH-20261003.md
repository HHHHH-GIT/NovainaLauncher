# LittleSkin 登录恢复 · 2026-10-03

## 修复

- 普通令牌续期不再发送 `selectedProfile`。该参数仅在登录返回未绑定令牌、用户首次选角色时使用；登录已绑定的角色直接保存，不重复绑定。
- 启动前先验证；401/403 后尝试一次正确的刷新。有效令牌距上次签发超过 12 小时也提前续期。12 小时是启动器维护策略，不代表 LittleSkin 的实际有效期。
- 启动器就绪后后台维护当前 LittleSkin 账户，每小时检查一次，仅到期时联网。游戏准备或运行、人工登录及数据迁移期间暂停；启动会等候已开始的续期，避免旋转游戏正在使用的令牌。
- 同账户验证/续期/保存/移除串行执行，重新登录保留账户 ID 和皮肤缓存。刷新返回角色名更新本地信息，拒绝绑定到其他 UUID。
- 验证请求每次限时 12 秒，网络异常、短时限流及 5xx 最多尝试三次；尊重 Retry-After，较长限流立即停止。认证/旋转请求不盲目重放，避免丢失响应后再次作废令牌。
- 临时服务错误不再提示“密码错误/登录失效”。已验证令牌的提前续期收到明确临时失败时保留原登录；真正撤销、过期或角色不匹配时要求人工重新登录。收到成功续期结果先保存 DPAPI 凭据，再处理取消。
- 退出及迁移取消并等待维护；迁移失败恢复维护。密码不保存，日志继续脱敏。

## Agent

`retry_account_login(account_id)` 使用查询返回的逻辑账户 ID，复用账户服务验证与恢复登录，不返回任何令牌，也不接收密码。

返回 `reauthentication_required` 时，Agent 调用 `open_account_login(account_id)`；LittleSkin 预填邮箱并打开人工登录面板。用户登录并返回后验证状态，再继续原任务。网络故障不等同于登录失效，不循环提交。真正恢复登录后允许重试因认证失败而未启动的游戏；成功启动等写操作仍去重。

## 有效性边界

令牌有效期和撤销由服务器决定，客户端无法保证永久有效。长期关闭启动器、撤销会话、密码或账户策略改变、其他客户端刷新同一令牌，以及服务端成功旋转后响应丢失，都可能需要人工登录。不使用保存密码或绕过认证来伪造有效状态。

协议依据：[Yggdrasil 令牌及刷新规范](https://yushijinhun.github.io/authlib-injector/en/yggdrasil-server-technical-specification.html)、[启动器登录恢复流程](https://yushijinhun.github.io/authlib-injector/en/launcher-technical-specification.html)。

## 验证与构建

- Downloads 编译 0 警告/0 错误，Windows x64 自包含 Release 发布成功。
- 集中检查 **21 项通过**：9 项 LittleSkin 模拟 HTTP/DPAPI 检查、11 项 Agent 协议/权限/恢复/队列检查及 1 项综合 WPF 离屏回归。覆盖已绑定角色不重复选择、首次绑定、取消后凭据保存、普通刷新不含绑定参数、角色改名、并发续期去重、临时故障重试、长限流、真正撤销、断流不重放及角色不匹配；Agent 恢复后重试失败启动且成功操作仍去重。
- 发布包：`bin/iKunLauncherNext-win-x64-1.0.0-ai-mode-auth-fix.zip`；源码包：`bin/iKunLauncherNext-source-1.0.0-ai-mode-auth-fix.zip`，含修复记录及依赖许可证，不包含用户账户、数据或游戏文件。
- 真实 LittleSkin 账户、实际长时间续期及真实 DeepSeek 工具调用未实测；模拟验证不代替这些验收。

```powershell
dotnet test tests/Launcher.Tests/Launcher.Tests.csproj -c Downloads -p:TestSdkVersion=17.8.0 -p:XunitVersion=2.5.3 -p:XunitRunnerVersion=2.5.3 --filter 'LittleSkinSessionTests|AgentModeTests|Navigation_Contains_Exactly_One_Page_And_Controls_Render'
dotnet publish src/Launcher.App/Launcher.App.csproj -c Release -r win-x64 --self-contained true -p:Optimize=true -p:DebugType=none -p:DebugSymbols=false -o bin/publish-1.0.0-ai-mode-auth-fix
py artifacts/package_welcome.py 1.0.0-ai-mode-auth-fix
```
