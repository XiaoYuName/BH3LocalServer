# 主线入口可见但提示等级锁定 · 1.3.4

## 实机现象

用户确认 1.3.3 主线入口已经出现，但点击后弹出“主线——新章节开放，解锁等级：1”，无法进入章节。
本次 272 条游戏请求全部 Handled，原先的 `MainStory ContainerData is null` 已消失，也未出现新的客户端空引用。
日志与截图保存在 `evidence/lobby/user-client-09.jsonl`、`user-output-09.txt`、`user-chapter-09.png`。

## 根因与上版结论更正

1.3.3 只补齐了世界地图容器与推荐入口。上一版文档认为 1661 章节组只影响活动，这一判断不完整：9.1 普通主线也通过章节组站点判断是否允许进入。

| 当前 9.1 静态位置 | 实际行为 |
| --- | --- |
| `LevelModule.OnGetChapterGroupGetDataRsp`，`0x7de3b40` | 将 1661 交给主线章节组的 Update |
| 主线组 `Update`，`0x6b21a90` | IsAll=true 时先清除所有站点状态，再应用响应列表 |
| 站点 `Clear`，`0x6fcbba0` | 状态重置为 Locked=1 |
| GroupType=1 工厂，`0x728cc20` | 构造普通章节组，更新分支位于 `0x616caf0` |
| `UpdateSingleSiteData`，`0x616cca0` | 按 site_id 更新对应站点 |
| 站点 `get_IsUnlocked`，`0x9185250` | 排除登录天数限制后，检查状态 >= Unlocked=2 |
| `ChapterDataItem.get_Unlocked`，`0x7ece210` | 通过章节组 `ChapterIsCanEnter` 判断章节可进入 |

旧包 1661 返回 `08 00 18 01 20 00`，表示成功、全量、查询 ID=0，但没有章节组。因此客户端每次全量更新后都保留 Locked 状态。提高玩家等级或伪造关卡通关不是此次修复所需条件。

本地参考表的映射为 `ChapterGroupConfig[1]` → `ChapterGroupSite[1]` → 第一章。GroupType=1，UnlockLevel=1，SiteType=1。来源哈希与静态片段见 `evidence/lobby/chapter-unlock-static-proof.json`；表 ID 来自已有参考数据，消费者来自当前 9.1 客户端。

## 服务端修改

新增 `CampaignService.ChapterGroups`，接管 1660/1661：

- 缺省或 ID=0：全量快照，返回已支持的章节组 1、站点 1、章节 1。
- ID=1：仅返回第一章，IsAll=false；其他指定 ID 回空列表并回显选择器。
- 第一章未完成时返回 Unlocked=2；已有存档完成末关 10115 后，查询返回 Finished=3。
- 查询不改变钱包、关卡完成状态、奖励或存档版本。第一章解锁后，各关卡仍检查等级、前置、体力和角色条件。

状态从现有 schema 3 进度计算，无须修改旧账号存档。首关 Progress=0、IsDone=false 保持未通关；不能把章节解锁当作预先完成剧情。

## 验证与发布

分层测试覆盖全量/指定查询、首章解锁但关卡未完成、下一关前置、首关成功进入、只完成一关时章节仍未通关、全章完成后状态与重启恢复、跨账号隔离。
隔离 KCP 用真实响应重放客户端的清除→更新→状态判断，覆盖 32/64 位帧及重启。此重放只验证静态消费条件，不执行游戏 UI。

发布目录 `dist/win-x64-1.3.4/`，继续包含原生登录器和服务端。原启动入口指向新版，设置、证书与已有账号存档迁移至新目录；原 1.3.3 保留。
四角色继续沿用 `evidence/lobby/` 下的 MODIFIED_FILE.zip、DIFF_FILE.patch、VERIFICATION.txt、ROLLBACK.sh；1.3.3 归档到 `revisions/1.3.3/`。

## 用户复验

完全关闭旧登录器和游戏，从根目录 `start-desktop.bat` 启动 1.3.4，检查出击→主线是否能进入第一章，随后测试 1-1 出击与结算。主线 UI 和实际副本仍由用户验收。

## 本轮自动验证结果

68 项分层测试通过。隔离 KCP 共 239 项：141 基础、40 副本、24 selector、4 客户端结构、3 overall、15 世界地图、12 章节解锁；全部通过。旧 1.3.3 相同的 12 项章节查询观察中，9 项首章解锁失败、3 项未开放组选择器通过。登录器与整包 16 项检查通过。设置、证书和账号存档已迁移，schema 3、逐表内容及完整性校验通过。
