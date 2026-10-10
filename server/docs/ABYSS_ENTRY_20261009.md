# 点击深渊卡住 · 1.5.1

## 实机证据

用户 1.5.0 日志 `server-20261009-133254-71236.jsonl` 中 5202 请求 678 次、5200 请求 1346 次，均 Handled。客户端 `output_log.txt` 则反复抛出 `MonoUltraEndlessLevelInfoPanelCmpt.RefreshCupLevel +0x7323337` 空引用。

静态核对本机 9.1 UserAssembly：异常返回地址对应 0x7323332 的空引用抛出调用，由 0x7322dc1 的条件分支进入。段位面板读取 `_mainRsp.last_settle_info.buffer_cup`，服务端没有返回第 7 字段。`get_LastSettleInfo` (0x8840ff0) 与 `get_InventCupNum` (0x8844ab0) 交叉确认偏移 +0x28 和 +0x20。

另一个缺陷是 5200 查询当前期 1028 和空期号 0，5201 总返回 1028。客户端 OnGetTopRankRsp (0x88492c0) 按响应期号写入缓存，导致期号 0 永远得不到数据。

## 修改

- `ChallengeService.AbyssInfo` 始终提供非空 LastSettleInfo，BufferCup=0；当前本地杯数保持 355，前后杯数一致，历史期号保持 0。无历史奖励和额外结算副作用。
- `ChallengeService.AbyssRank` 和 `LobbyHandlers.CreateChallengeHandlers` 保留请求期号。只有当前期返回当前得分；历史空期及未知期返回对应键的空排行。
- 分层测试验证 protobuf 序列化后对象存在、当前和历史期号隔离、查询不改变钱包和进度；原生 KCP 回归覆盖空请求与显式期号、客户端缓存所需响应。

## 验证与发布

144 项分层测试、593 项原生 KCP 协议检查（含 104 项玩法检查）和 19 项整包检查通过。

具体命令、原始输出、状态、哈希和对照见 `../evidence/lobby/VERIFICATION.txt` 的 abyss_entry_delivery。源代码归档、补丁及可执行回滚继续复用同目录四角色文件。1.5.0 原角色归档于 revisions/1.5.0。

源码对照输入为 `abyss-entry-client91`：发送 5202 空包、5200 期号 1028、5200 空包。1.5.0 缺结算对象且排行缓存不完整；1.5.1 两个前置条件均满足。原始 BASELINE 不支持该系统；对独立副本执行 ROLLBACK 后恢复原始行为及哈希。

新版位于 `dist/win-x64-1.5.1`，日常启动脚本已经指向新版。存档从 1.5.0 用只读 SQLite 一致性快照迁移并逐表核对，原包保留。自动验证不等于实机画面通过；按既定分工由用户关闭旧游戏及登录器，再从新版复验深渊入口。
