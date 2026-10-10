# BH3 9.1 服务端

Windows x64 / .NET 10，面向崩坏 3 PC 国服 9.1.0 的独立游戏服务器工程。
[当前状态](docs/STATUS.md)记录实现与验收范围，[文档索引](docs/README.md)提供开发入口。

工程按 Protocol、Game、Persistence、Server 四层维护，支持独立构建、测试、发布和存档。
1.3.0 在已实机通过的大厅基础上，新增第一章普通主线关卡、进入、通关结算、任务/星级领奖与 SQLite schema 3 事务持久化。
真实 9.1 大厅已由用户验收；副本画面待用户验收。当前协议属于兼容候选，`gameplayReady` 保持 `false`。详见 [登录与大厅专项](docs/LOGIN_LOBBY_20261009.md)。

新版独立发布于 `dist/win-x64-1.6.3/`，关闭旧版后打开新版 `Launcher/BH3.Launcher.exe`。

日常使用 [启动登录器](start-game.bat)，点击“启动服务”即可自动托管同包服务端。完整包说明见 [一体发布](docs/BUNDLE_RELEASE_20261009.md)。

1.5.0 扩展普通主线目录、试用角色和剧情记录，接通本地记忆战场与 81 级以上超弦空间的结算、任务领奖。覆盖与未完成范围见 [玩法专项](docs/GAMEPLAY_20261009.md)。

## 目录

```text
server/
  BH3.slnx                  解决方案
  src/BH3.Protocol/         报文编解码
  src/BH3.Game/             会话、分发、玩家用例
  src/BH3.Persistence/      SQLite、迁移与事务
  src/BH3.Server/           组合根、UDP/HTTP、预检和日志
  tests/BH3.*.Tests/        对应分层回归
  config/server.json        源码配置模板
  docs/                    架构、规范、状态与专项
  tools/                   发布版隔离验证
  evidence/skeleton/       本轮原始输出和验证记录
  dist/win-x64/Launcher/   原生登录器
  dist/win-x64/Server/     Windows x64 服务端
  dist/win-x64/Start.bat   整包启动入口
```

## 构建与启动

开发使用 .NET SDK 10.0.401，由 `global.json` 固定。在本目录执行：

```bat
build.bat
test.bat
publish.bat
start-game.bat
```

发布包自带运行时，包含 Launcher 与 Server。`start-game.bat` 打开登录器，登录器自动启动同包服务端；`start-server.bat` 仅供开发时单独调试。
源码直接运行时建议显式传入配置：

```bat
dotnet run --project src/BH3.Server -c Release -- --config config/server.json
dist\win-x64\Server\BH3.Server.exe --check
```

`--check` 只读检查配置和已有数据库版本，不创建库、不监听、不判断端口是否空闲。
启动成功输出一行 `server.ready` JSON；Ctrl+C、重定向 stdin 的 `stop` 或 EOF 都会正常停服。

## 默认端口与路径

| 用途 | 默认值 | 说明 |
| --- | --- | --- |
| 游戏入口 | UDP 127.0.0.1:21000 | 握手、KCP、认证与大厅初始化 |
| 宿主健康 | HTTP 127.0.0.1:21080 | `/health/live`、`/health/ready`、`/api/status` |
| SDK / dispatch | 登录器已有 TCP 20100 | 继续由桌面登录器托管 |
| HTTPS 代理 | 登录器已有 TCP 8080 | 继续由桌面登录器托管 |
| 玩家库 | `../data/bh3.db` | 相对于所用配置文件目录解析 |
| 日志 | `../logs` | 每次进程启动写独立 JSONL 文件 |

默认发布配置对应 `dist/win-x64/Server/data/bh3.db` 和 `dist/win-x64/Server/logs/`；源码显式配置对应 `server/data/` 和 `server/logs/`。
两套路径独立。发布包不包含测试玩家或测试数据库。升级前先停服并备份存档；发布脚本保留已有发布配置。

## 接入登录器

保持“自定义服务端”为空即可自动定位同包 Server。游戏 UDP 端口由登录器通过参数传递，无需重复修改两个配置。
登录器会关闭自身的内置握手入口，托管本进程并收集标准输出。详见 [登录器接入](docs/LAUNCHER_INTEGRATION.md)。
首次发布自动迁移旧设置与存档，原目录保留。供分发的 ZIP 不含这些本机数据。

## 验证

`test.bat` 运行四个 xUnit v3 测试工程，失败时返回非零退出码。测试使用临时数据库和随机端口。
发布版验证命令见 [完整回归](docs/FULL_REGRESSION.md)；验收分别记录编译、分层测试、发布进程和真实客户端结果。

工程分层及文档组织参考 115CN，协议、数据库和玩法没有照搬 DNF 字段。参考映射见 [参考工程指南](docs/REFERENCE_GUIDE.md)。

最新实现：[第一章出击与结算](docs/CAMPAIGN_20261009.md)。

1.3.2 修复本地账号误触旧实名绑定入口后的卡住，详见 [专项说明](docs/REALNAME_DIALOG_20261009.md)。更新后完整退出旧登录器和游戏，再从原入口重新登录。

1.3.2 实名弹窗已由用户实机验收。1.3.3 补齐出击主线地图与推荐入口，详见 [出击页专项](docs/WORLD_MAP_20261009.md)。

1.3.4 修复主线入口可见但锁定：补齐第一章章节组站点状态，详见 [章节解锁专项](docs/CHAPTER_UNLOCK_20261009.md)。

1.3.5 修复点击战斗准备后持续等待：47/48 编队回包只含实际队员，详见 [准备页专项](docs/TEAM_PREPARE_20261009.md)。70 项分层、254 项离线协议及 16 项整包检查通过；实际出战选择与副本仍由用户验收。

1.3.6 修正首关结算被拒绝与前置未通关：按 9.1 省略字段的语义处理 WIN=1，错误回包不写非法枚举，详见 [结算专项](docs/SETTLEMENT_20261009.md)。74 项分层、281 项离线协议、16 项整包检查通过；旧失败结算需在新版重打一遍首关复验。

1.3.7 修正结算后的 `PjmsGetCurWorldRsp.Retcode.FAIL` 弹窗，详见 [返回世界专项](docs/PJMS_CURRENT_WORLD_20261009.md)。原入口启动 `dist/win-x64-1.3.7/`，已有三关通关与奖励按原样迁移。

1.3.8 修正章节挑战奖励领取后仍显示可领取，详见 [领取状态专项](docs/ACT_REWARD_STATE_20261009.md)。先推送已提交的 457 领取状态，再发送 459 领取结果；已有记录保留，重复领取不发奖。原入口已指向 1.3.8，78 项分层、320 项隔离协议及 16 项整包检查通过，实机界面由用户验收。

## 1.3.9 已捕获账号数据导入

当前一体包 `dist/win-x64-1.3.9/` 支持角色、技能、武器、圣痕的离线导入，以及1至3人自有角色编队和经验持久化。导入同步舰长等级，保留本地余额与通关、领奖记录；原存档自动备份。见 [导入专项](docs/ACCOUNT_IMPORT_20261009.md)。

当前 [1.4.1 商店与补给配置](docs/SHOP_SUPPLY_20261009.md)：GM 支持商店开关、商品上架与排序、补货；游戏购买原子扣款发放并同步库存。补给三类入口、开关及开放时间由 GM 管理。

当前包：1.4.2；商城、充值、公告与奖励覆盖限制见 [专项文档](docs/MALL_20261009.md)。

1.5.1 修复超弦空间入口空引用与排行期号错配，见 [深渊入口专项](docs/ABYSS_ENTRY_20261009.md)。

1.5.2 [协同者 / 人偶与补给入口](docs/COMPANIONS_SUPPLY_20261009.md)：养成、出战、技能开关、GM、邮件、协同者补给及捕获数据补入，保留本地余额和历史进度。

最新修复：[月卡、商城、补给存档与晋升 · 1.5.3](docs/ECONOMY_20261009.md)。修正月卡状态、礼包扣款/数量、商店中文键、当前角色与碎片存档，并接通晋升。86 项商城商品仍缺奖励定义，配置前不可购买；实机 UI 由用户复验。

最新：[本地系统修复 · 1.6.0](docs/SYSTEMS_20261010.md)。186 项分层、785 项原生 KCP、19 项整包检查通过。通行证客户端入口与神之键 209 的新增规则仍缺 9.1 配置，不视为全量复原。

最新：[大厅回归修复 · 1.6.1](docs/LOBBY_REGRESSION_20261010.md)。192 项分层、817 项 KCP、19 项整包检查通过。开放抓包活动配置，完整活动玩法及新礼包明细的限制见专项文档。
