# 章节挑战奖励领取状态同步 · 1.3.8

## 用户现象与日志

第一章挑战奖励领取成功后仍显示可领取。1.3.7 最新日志共 274 条请求，全部 Handled。11:13:53 的 458 批量领取 act 101 的档位 1、2、3，459 返回成功；11:13:57 重复领取返回 HasTake。
只读存档确认 `ClaimedActRewards` 已有 `101:1`、`101:2`、`101:3`。服务端保存和防重复发奖正常，问题是成功回包序列只有 `459,11,42,113,25,27`，未推送客户端使用的 457 领取状态缓存。

证据保留于 `evidence/lobby/user-client-13.jsonl`、`user-launcher-13.txt`、`user-output-13.txt`、`user-act-reward-13.png` 及 `act-reward-baseline-manifest.json`。已有通关、奖励、领取记录和未完成战斗按原样保留，不补发奖励或手改玩家状态。

## 客户端静态依据

| 位置 | 行为 |
| --- | --- |
| `LevelModule.OnGetStageActDifficultyRsp`，`0x7de1430` | 从 457 的 ActDifficultyInfo.has_take_challenge_num_index 更新模块领取缓存 |
| `LevelModule.HasActChallengeTaken`，`0x7de6f50` | 奖励入口通过此缓存判断是否已领取 |
| `CASelectPageContext.OnTakeStageActChallengeRewardRsp`，`0x979d570` | 459 成功或部分成功时立即刷新入口，未更新 LevelModule 缓存 |
| `ActChallengeDialogContext.OnTakeStageActChallengeRewardRsp`，`0x7e46850` | 用成功档位更新对话框行，未修复模块缓存 |

客户端文件哈希、具体反汇编和结论见 `act-reward-static-proof.json` 与 `act-reward-static/`。仅分析磁盘文件，未启动或调试游戏客户端。

## 服务端修改

`LobbyHandlers.Change<T>` 在 `TakeStageActChallengeRewardRsp.Retcode` 为 `Succ` 或 `HasTake` 时，读取已提交的章节领取状态，并将 `457 GetStageActDifficultyRsp` 放在 `459 TakeStageActChallengeRewardRsp` 前。

| 分支 | 响应顺序 | 效果 |
| --- | --- | --- |
| 首次成功领取 | `457,459,11,42,113,25,27` | UI 刷新之前已获得领取状态；原钱包和进度快照继续发送 |
| 已领取重试 | `457,459` | 修复旧页面缓存，保留 HasTake，不重复发奖 |
| 非法请求或星数不足 | 原有失败响应 | 保留验证规则，不伪装成功 |

`CampaignService.ClaimAct` 原有持久化事务保持不变；领取记录来自当前认证账号。当前服务尚无部分成功发奖分支，本次不改变批量请求的事务语义。

## 验证与发布

新增单档和批量两个回归，按回包顺序模拟客户端缓存，检查 459 回调时已经可见正确档位；覆盖重载、重试不增发、账号隔离、认证和非法难度。旧实现两个测试均失败，新实现通过。24 项独立原生 KCP 专项覆盖 32/64 位帧、单档、批量、重启和钱包/材料不变：旧 1.3.7 为 17 项通过、7 项状态同步失败；新版 24 项全部通过。

全部结果为 78 项分层测试、320 项隔离协议检查及 16 项登录器/整包检查通过。详见 `act-reward-tests-138.txt`、`published-act-reward-138.json`、`relocated-package-138.json` 与 `VERIFICATION.txt`。这些是自动化协议验证，不代表已实机验收界面。

一体包位于 `dist/win-x64-1.3.8/`，包含原生登录器与同包服务端。配置、证书、账号和最新存档从 1.3.7 复制，schema 3、逐表一致性及 SQLite 完整性检查见 `migration-138.json`。旧目录保留，分发 ZIP 不含本地存档。

继续使用 MODIFIED_FILE.zip、DIFF_FILE.patch、VERIFICATION.txt、ROLLBACK.sh 四个交付角色；1.3.7 角色保留在 `revisions/1.3.7/`。累积 BASELINE/ROLLBACK 为原始骨架，同输入下无领取同步；MODIFIED 有同步。1.3.7 的具体故障另用保存源码和发布包对照，确认记录已经保存但缓存未推送。回滚只操作独立源码 ZIP 副本，不替换运行目录或玩家数据。

## 用户复验与后续

退出旧游戏和登录器，从根目录 `start-desktop.bat` 启动 1.3.8。进入第一章后检查原已领取奖励，再领取其他满足条件的档位，观察入口是否立即变为已领取；重登后检查状态是否保持。实机操作与验收由用户执行。
此前日志中的 FinishPlotReq=1378 仍是独立的待实现项；本次最新日志没有未处理请求，未以新增通用成功回包代替领奖状态同步。
