# 本地购买账号资料检查 · 1.4.3

用户实机点击充值商品时提示“为了保障您的账号安全，请先绑定实名信息”。本轮日志中新版 SDK 密码登录成功，但没有 6731 / 1494 / 207 购买请求；因此问题位于客户端发起购买之前。

## 根因与修改

只读分析 9.1 原始客户端：游戏账号适配器通过 login_is_account_real_name 读取 SDK 状态。SDKWinMiHoYoSDKDll 的 RVA 0x100020d0 检查 AccountModel.identity_card (+0x48) 或 realname (+0x40) 是否非空。is_adult=1 与 1.3.2 的 dispatch.is_xxxx 均不补齐该账号字段。

AccountPlatNative.dll 的 0x32cef0 从新版响应 user_info 读取账号，调用 0x313ae0 解析 realname（字段访问 0x314227），随后读取 identity_code（0x32d510）。旧版 MDK 对应 account.realname / account.identity_card。之前两条本地登录响应均没有姓名字段。

LocalApi.Handle 现在在两条响应的正确层级返回 realname="本地测试"；证件号保持空字符串。覆盖密码登录与票据重登。此数据仅为本地模拟购买所需的测试账号资料，未认证或修改任何官方账号。combo 数据未新增无效的 login_is_account_real_name 字段。原商城奖励、限购和订单处理保持不变。

## 验证与边界

- 120 项分层测试、485 项原生 KCP 检查（44 项商城）、19 项整包检查全部通过。
- 新增 SDK 响应检查覆盖 6 条登录/重登路径；账号 ID、用户名和签名票据保持一致。
- 反例覆盖只填成年标志、错误 JSON 层级、空/缺失姓名与证件、错误 combo 风格标志。
- 对照工具编译每个源码包内原始 LocalApi，并对同样请求执行客户端静态分析得到的账号判定：BASELINE 与 1.4.2 为 false；MODIFIED 为 true；ROLLBACK 为 false，回滚哈希恢复原值。这是离线响应契约验证，不等于实机 UI 验收。
- 未启动或控制游戏。需要用户退出游戏和旧登录器，从项目 start-desktop.bat 启动 1.4.3 后重新登录再购买，刷新 SDK 缓存。

## 存档与交付

当前运行的 1.4.2 存档通过 SQLite 只读连接和固定读事务做一致性快照，迁移至新建 1.4.3 目录，7 张业务表逐行相同，完整性与 schema 4 通过。快照中舰长 88 级、金币 1882750、水晶 225、体力 121，9 个通关关卡及 11 条结算回执保留。原数据库快照前后 SHA 相同，原目录保留。

一体包：server/dist/win-x64-1.4.3；无玩家数据分发包：server/dist/win-x64-1.4.3.zip。源码、差异、验证、回滚沿用 evidence/lobby 的四个角色文件；补充证据位于 evidence/lobby/payment-143，上一版四角色原样存放于 revisions/1.4.2。
