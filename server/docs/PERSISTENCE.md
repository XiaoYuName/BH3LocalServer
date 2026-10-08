# 玩家存档与迁移

## 当前结构

SQLite `user_version=3`，`application_id=0x42483353` 标识 BH3.Server。

| 表 | 字段 | 约束 |
| --- | --- | --- |
| account | uid、created_utc | uid 为正整数主键 |
| player_profile | uid、nickname、revision | uid 外键；名称 1–32 字符；revision 非负 |
| player_lobby | uid、data_json | uid 外键；基础大厅状态及完成引导 |
| client_data | uid、type、id、data | 联合主键；每账号至多 128 项，每项至多 64 KiB |
| player_campaign | uid、data_json | 关卡、出击、材料及领奖记录 |
| stage_receipt | uid、fingerprint、response | 联合主键，结算幂等回执 |

有效本地票据通过后建档；已存在的玩家不会被初始化覆盖。密码与 token 不落库。schema 1/2 自动升级至 3，保留昵称与修订号。
`CompleteGuides` 在写事务中合并去重引导列表，只更新会话所属账号；无效报告不部分写入。

原有大厅 JSON 兼容新增金币、经验、角色经验和体力计时字段；迁移建表不覆盖原始 JSON。

## 事务

`SqlitePlayerStore.Create` 在同一事务中创建 account 和 player_profile，第二步失败时第一步回滚；重复 UID 不覆盖原档案。
`Rename` 使用 `WHERE uid=$uid AND revision=$expected`，成功时 revision 加一；过期返回 Conflict，不存在返回 Missing。
同一玩家两个相同 revision 的并发修改只能有一个成功。业务层使用会话 UID，不提供任意玩家写入入口。

## 启动与版本

`SchemaMigrator.Initialize` 在写事务中检查版本和应用标识，重复启动不重复建表，更高版本或其他应用库拒绝。
新库创建与版本号提交共事务。连接统一启用外键，忙等待 5 秒，连接不使用池以明确释放文件句柄。
宿主持有 `bh3.db.host.lock` 的独占句柄，避免两个本地实例操作同一个正式存档。

## 验证与后续

自动检查新建、重复启动、外键、更高版本拒绝、其他库拒绝、注入第二步失败、CAS 竞争和重新打开。
发布探针另外检查实际 EXE 重启后保留隔离档案。
第一章关卡与领奖使用独立 Campaign 模型和追加迁移；其余玩法按相同方式扩展。

`LobbyStore.Campaign` 将大厅钱包、玩法状态和结算回执同事务提交。schema 3 出击详情见 [第一章专项](CAMPAIGN_20261009.md)。
