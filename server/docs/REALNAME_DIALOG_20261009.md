# 本地账号实名弹窗卡住修复 · 1.3.2

## 实机反馈与修正

用户确认 1.3.1 仍出现“实名注册 / 前往绑定”。09:37 的新版登录器日志记录了 query_gameserver 成功响应；本轮 224 条游戏请求全部 Handled，客户端六次记录相同按钮回调的 NullReferenceException。

1.3.1 把环境标志放在了错误的 `server_ext.is_xxxx` 路径。此前测试只验证该字段存在，没有模拟客户端解码，因而没有发现错误。上一版“标志能被客户端读取”的结论撤回；其源码、报告和四角色保存在 `evidence/lobby/revisions/1.3.1/`。

1.3.2 在原生登录器 `LocalApi.Handle` 中改为 `ext["98533854"]="1"`，撤去无效的 server_ext 写入。9.1 客户端先将数字键还原为 `ext.is_xxxx`，再写入弹窗前置判断使用的字段。SDK 账号类型、签名票据、实名状态和原始资源文件不变。

## 客户端静态证据

| 步骤 | RVA / 数据 | 已确认行为 |
| --- | --- | --- |
| 解密 | `0x3f5d1c0` | 解密加密 dispatch 时设置后续键转换标志 |
| 键转换入口 | `0x3f5dbf0` | 调用 `0x3f5e200` 转换 JSON 中的 ext |
| 转换字典 | `0x71de690` | 数字 literal 78397 `98533854` 对应静态字段 +0x286e8 |
| 静态字段初始化 | `0x71e0da0` | +0x286e8 来自 literal 78483 `is_xxxx` |
| 容器读取 | `0x7d8ca05` | RIP 指向 `0x2754508`，以 literal 槽基址 `0x2740470` 解析，得到 index 10259 `ext`；不是 index 91310 `server_ext` |
| 字段写入 | `0x7d8cb83`、`0x7d8cbd5` | 将解码后值解析为整数，与 1 比较，写入 DispatchSeverData +0x15a |
| 弹窗判断 | `0x4006d18` | +0x15a 非零则直接返回，不创建该旧账号弹窗 |
| 按钮异常 | `0x6490850`、异常栈 `0x6490bdc` | 回调要求旧账号适配器 KGCJGGHLNJJ；本地 SDK 适配器不满足该强转 |

从实际指令操作数恢复了 86 组数字键映射，保存为 `desktop/Resources/client91-dispatch-keys.json`。测试读取这份独立的客户端数据，重放 ext 转换再检查环境标志。客户端转换只保留可映射的键，因此直接写 `ext.is_xxxx` 也不能替代线上数字键。

该环境标志还被礼包推荐、购买入口和设备标识格式化读取；本地服不提供真实支付或实名认证服务。完整客户端画面仍由用户验收，接口测试不等于实机已通过。

## 验证结果

- 同一解码探针重新编译执行旧 1.3.1 的 LocalApi：两个端点、两种支持的加密版本，四组结果均为“错误层级有 1，客户端解码后标志缺失”。
- 修改版同输入得到数字键 98533854，解码后 is_xxxx 为 1；原始基线及回滚副本均未设置该标志，回滚哈希相同。
- 新增反例：错误 server_ext 层级、未映射明文键、值 0 / 2 / true 均不得启用该客户端分支。
- 校验其余 ext、完整 server_ext、资源清单和资源地址列表保持原值；本地签名票据仍有效。
- 清洁整包异地解压后，11 项登录器和 5 项进程托管检查通过，其中 HTTPS CONNECT 响应经过同一客户端键转换检查。
- 65 项服务端分层测试，以及新发布 EXE 的 141 项既有 KCP、40 项副本、24 项查询标志、4 项响应结构、3 项 overall 检查全部通过。

自动检查只运行隔离服务和源码探针，没有启动游戏或改写系统代理。实际弹窗消失及副本进入、通关结算仍待用户复验。

## 发布与存档

根目录 `start-desktop.bat` 已指向 `server/dist/win-x64-1.3.2/Launcher/BH3.Launcher.exe`。分发压缩包为 `server/dist/win-x64-1.3.2.zip`。

已从 1.3.1 复制设置、DPAPI 证书、服务端配置，并通过 SQLite backup 复制账号 10001 的 schema 3 存档，逐表核对、integrity_check 通过。旧版目录和存档保留。

用户复验：完整退出游戏与旧登录器，从原入口重新启动，确认界面版本 1.3.2，重新登录使 dispatch 重新加载。确认弹窗不再阻塞后，继续“出击 → 第一章 → 进入副本 → 通关 → 结算”。

## 证据与回滚

本轮报告：`evidence/lobby/realname-static-proof-132.json`、`realname-baseline-131.json`、`published-realname-132.json`、`relocated-package-132.json`、`migration-132.json`。最新用户日志为 `user-client-07.jsonl`、`user-output-07.txt`、`user-launcher-07.txt`。

继续复用 `MODIFIED_FILE.zip`、`DIFF_FILE.patch`、`VERIFICATION.txt`、`ROLLBACK.sh` 四角色。ROLLBACK.sh 仅恢复传入的源码 ZIP 副本，不操作运行中的服务或玩家存档。验证记录明确区分源代码观察、接口解码重放和用户实机结果。

第一章实现范围与限制见 [第一章专项](CAMPAIGN_20261009.md)。

## 后续实机结果

用户在本轮明确确认 1.3.2 实名弹窗正确消失。后续出击空白属于世界地图容器缺失，处理记录见 [1.3.3 专项](WORLD_MAP_20261009.md)。
