# 结算后返回世界错误 · 1.3.7

## 用户现象与日志

1.3.6 实机结算后显示 `proto.PjmsGetCurWorldRsp+RetcodeFAIL`。最新日志共有 394 条请求，392 条 Handled；两次未处理的请求为 FinishPlotReq=1378。
10101、10102、10105 三次 StageEnd 均返回 Succ/StageWin，并推送钱包、关卡和任务快照。只读存档确认三个关卡各 Wins=1，结算回执共 3 条，活动战斗为空。此次错误发生在结算成功之后的 7702 查询。

原始证据保留于 `evidence/lobby/user-client-12.jsonl`、`user-launcher-12.txt`、`user-output-12.txt` 和 `user-pjms-12.png`。此前通关和奖励有效，不需要为了本次修复清档或重打首关。

## 客户端静态依据

| 位置 | 行为 |
| --- | --- |
| `PrepareReEnterCurrentWorldTask.OnExecute`，`0x575b2d0` | 等待 7703 并发送 7702 |
| `PrepareReEnterCurrentWorldTask.OnPacket`，`0x575b4a0` | retcode 非零时显示错误并使任务失败；成功时把 world 传入 SetLoadWorldInfo，随后完成任务 |
| `WorldManager.SetLoadWorldInfo`，`0x696dfa0` | 检查 WorldLoadInfo.Valid；空世界状态不建立 PJMS 场景加载目标 |
| `WorldLoadInfo.Valid`，`0x613fde0` | world 非空且 world_id 不等于 InvalidWorldID 才有效；配置构造默认 InvalidWorldID=0 |
| `ReloadWorldOnReconnectTask.OnPacket`，`0x4a4a9c0` | 成功分支直接读取 world，不能只返回成功码而省略对象 |
| 响应 reader/writer，`0xf1f2db0` / `0xf1f2a70` | field 1 为 retcode，field 2 为 PjmsWorld |

详细反汇编与文件哈希见 `pjms-current-static-proof.json`、`pjms-current-static/`。只读取磁盘文件，没有运行或调试客户端。

## 服务端修改

`LobbyHandlers.CreateCampaignHandlers` 的 7702/7703 分支改为返回 SUCC，包含 `world.world_id=0`。这表示查询成功且普通主线账号没有活动 PJMS 世界。响应为 `08 00 12 02 08 00`。
`PjmsGetCurWorldRsp` 补回遗漏的 world 字段，复用既有 PjmsWorld 类型；7707 登录快照和 7703 查询使用同一空世界构造函数。查询仍要求认证，不改变奖励、关卡、回执或活动战斗。

上一版将 FAIL 当作未开放功能的正常答复是错误判断。现已用客户端实际返回任务分支取代这一假设。没有把账号指向参考服务端的世界 400，也未实现 PJMS 世界探索、进入或活动世界断线重连。

## 验证与发布

新增两项分层回归分别覆盖结算前、结算后查询：旧实现均失败，修改后验证返回码、对象存在、显式零 ID、与初始化一致、重复查询和存档重载不改变进度。
15 项独立 KCP 检查覆盖 32/64 位帧以及服务重启：旧 1.3.6 有 12 项失败、3 项无副作用检查通过。修改后的结果与全部既有回归一起记录于 `published-pjms-current-137.json` 和四角色 `VERIFICATION.txt`，总计 76 项分层、296 项协议、16 项整包检查。

一体包位于 `dist/win-x64-1.3.7/`，仍包含原生登录器和同包服务端。账号、设置、证书从 1.3.6 复制，逐表比对、schema 3 和 SQLite 完整性检查见 `migration-137.json`。旧目录保留，分发 ZIP 不含本地账号数据。
沿用 MODIFIED_FILE.zip、DIFF_FILE.patch、VERIFICATION.txt、ROLLBACK.sh 四个角色；1.3.6 文件归档于 `revisions/1.3.6/`。BASELINE 与 ROLLBACK 指向原始骨架，1.3.6 的具体故障另作同输入对照。

## 用户复验与后续

关闭旧游戏和登录器，从根目录 `start-desktop.bat` 启动 1.3.7。已有三关进度保留；可继续下一关或任选已开放关卡，检查结算后的返回、再次出击和重登进度。
客户端界面最终行为仍由用户实机验证。日志中新出现的 FinishPlotReq=1378 单独留待剧情协议补齐，不将它与当前明确的 PJMS FAIL 混为同一根因。
