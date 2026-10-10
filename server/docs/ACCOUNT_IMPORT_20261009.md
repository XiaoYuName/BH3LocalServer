# 已捕获角色与装备导入 · 1.3.9

用户于2026-10-09授权导入本地存档，并选择“同步抓包中的舰长等级，保留本地余额”。目标是原本地账号10001，原昵称、账号ID、货币、体力、消耗材料、队伍、引导、主线进度及领奖回执保留。

## 已导入范围

来源是 `D:/Game/BH3/capture-20261009-131730-89d321/account-copy.json`，SHA-256为 `819307f525debeb03aab73fa3c8ada7571323af3b9a078f78146f8c325837861`。
成功全量响应11、25、27作为起点，按捕获顺序应用实体增量，结果为102个角色、330件武器、547件圣痕、舰长等级88。角色技能、等级、星级、武器关联、圣痕关联和词条，以及实体内未知protobuf字段保留。

角色512的技能列表缺失且星级为0，保持实样，不填充或宣称它已可正常出战。原采集TransportComplete/CoreSnapshotComplete仍为false；本次是明确允许缺失连接起点的已复核导入，不将它改标为完整账号备份。

不导入官方账号认证信息、UID、昵称、钱包、消耗材料、回归/抽卡状态及其他未实现系统。登录活动、抽卡结果用于先前分析，不作为本地主线奖励重放。

## 实现与行为

- `AccountCopyImport.Read/Apply`：检查版本、单来源账号、显式成功码、全量快照、时间顺序、同包重复ID和装备引用；拒绝缺少全量、悬空引用、未初始化本地UID、进行中的本地战斗或覆盖另一来源。
- `player_inventory`（schema 4）：保存角色和装备protobuf及来源哈希。与本地等级更新共用SQLite事务；重复同来源导入不重置等级、经验、余额或进度。
- `LobbyHandlers.Avatars/Equipment`：全量、零ID和定向查询均读取持久化库存。消耗材料继续来自本地主线存档。
- `CampaignService.Begin/SetTeam/ApplyAvatarExp`：校验1至3名自有角色；经验只写入参战角色，满级角色不因本地限制降级；结算重试复用已有回执。
- 离线命令与服务端共用数据库独占锁。导入前自动建立SQLite备份，不启动游戏、代理或网络监听。

```text
BH3.Server.exe --config config/server.json --import-account-copy <account-copy.json> --uid 10001 --allow-partial
```

默认同步舰长等级和经验，保留本地余额；`--keep-local-level`可保留本地舰长等级。原始采集文件不会被写入。

## 本地交付与验证

新版位于 `server/dist/win-x64-1.3.9/`，原启动入口 `start-desktop.bat` 已指向该包。登录器设置和证书已迁移；干净发行ZIP不含个人存档或抓包。
正式存档 `server/dist/win-x64-1.3.9/Server/data/bh3.db` 已导入，金币20250、水晶75、体力63为导入时保留值。account、player_profile、player_campaign、stage_receipt、client_data逐表一致；4条结算回执及挑战领奖记录保留。

84项分层测试、336项隔离协议检查（含真实样本经32/64位KCP分片传输、导入后重登、三人编队与结算）、16项异地整包检查、13项实样对象检查通过。后续游戏画面及实际战斗由用户验证；未宣称战斗伤害与官方完全一致。

原1.3.8目录及存档保持原样；正式导入另生成同目录 `.before-import-*.bak`。原四角色仍在 `server/evidence/lobby/`，增量记录在 `account-import-139/`，前一组角色保存在 `revisions/pre-account-import/`。
`ROLLBACK.sh`继续支持独立源码ZIP副本，并可用同目录 `ACCOUNT_IMPORT_BASELINE.db` 恢复另命名的存档副本；拒绝直接覆盖运行用的 `bh3.db`。回滚演练在独立副本完成，不撤销正式导入。
