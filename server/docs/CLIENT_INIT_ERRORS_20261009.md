# 2026-10-09 跳过引导视频后卡住 · 1.2.2

## 用户现象与日志

用户在 1.2.1 跳过引导视频后停留在登录舱门。保留日志为 `evidence/lobby/user-client-02.jsonl` 和 `user-output-02.txt`。
服务端本次处理 203 条请求、7 条 Unsupported；没有 session.error。两次 FinishGuideReportReq 都已有 130 响应。心跳与同步时间持续，不能把本次卡住归因于引导上报无回包。
客户端在消费服务端初始化数据时记录四组异常。截图说明尚未进大厅，但不单独证明某一个请求是唯一阻塞点。

## 修复内容与证据

| 响应 | 日志/静态证据 | 修复 |
| --- | --- | --- |
| GetPlayerCardRsp · 481 | OnGetPlayerCardRsp 数组越界；RVA 0x68bd530 内直接读取固定槽位 | 返回 3 个角色、2 个勋章、1 个 ELF 展示槽及 MsgData；未拥有的槽位为 0 |
| GetGachaDisplayRsp · 4703 | 客户端明确拒绝 GachaType wire-value 0 | 全量查询省略 type 字段；合法指定类型回传，未知类型失败且不编码无效枚举 |
| GetExBossInfoRsp · 511 | NotOpen 分支仍读取 BossInfo；0x9e7605d..0x9e76092 | 保持 NotOpen，补空 BossInfo，不开放活动或发放次数 |
| GetOpenworldMechaDefenseRsp · 4515 | 混淆方法 GEELLGPFILN.OnPacket 空引用；跳表将 4515 映射至 0x697c39e，随后解引用子对象 | 补 MechaDefense，剩余次数为 0 |

静态证据来自本机 UserAssembly.dll，对照客户端堆栈返回地址分析；未运行游戏、注入、附加调试或修改客户端。反汇编与跳表保存在 `client-exceptions-02.asm.txt` 和 `client-dispatch-02.json`。
名片空展示槽不等于解锁角色/勋章/ELF。补给仍返回空卡池，不提供抽卡或付费逻辑。

新增显式请求：8250 SimplifiedGodWarGetActivity、1713 GetWorldMapRecommend、3419 GetOpenworldEndlessData、4500 GetOpenworldStory、2647 GetWareHouseData、2456 ChatworldGetDishInfo。
仍未确认命令 7842 的定义，保持 Unsupported；不猜测相邻响应号。累计 128 种已知缺失请求得到显式处理，注册入口总计 155 个。

## 验证范围

- `tests-client02-fixed.txt`：49 项分层测试通过，Release 构建 0 警告、0 错误；最初枚举成员命名错误保留于 `tests-client02.txt`。
- `contracts-baseline-121.json`：同一组原生 KCP 请求在旧 1.2.1 上检出四处响应结构问题。
- `native-client02.json`：修改版的 140 项链路/初始化检查通过，四处响应结构检查全部通过。
- `published-client02.json`：发布 EXE 执行相同验证。
- `relocated-package-122.json`：分发 ZIP 异地解压后的登录器 9 项、整包 5 项检查。

原生验证报告中 `passed` 描述核心链路检查；`client_contracts` 单独保存四处字段/槽位检查的结果，交付脚本要求新版全部为 true。它们验证了报文结构，不代表实际客户端 UI 已成功进入大厅。

## 发布和下一步

本机包位于 `dist/win-x64-1.2.2/`，从 1.2.1 复制设置和数据库并核对记录；旧版目录保留。日常 `start-desktop.bat`/`server/start-game.bat` 优先打开 1.2.2 登录器，由登录器自动启动服务端。
干净分发包 `dist/win-x64-1.2.2.zip` 不含本机账号、设置或存档。
关闭旧登录器后由用户实机复验跳过视频及大厅；如仍卡住，对照新 output_log.txt 的异常与 Server/logs 继续定位。
四角色交付仍位于 `evidence/lobby/`；1.2.1 角色副本保存在 `evidence/lobby/revisions/1.2.1/`。旧基线与存档不被回滚脚本改写。
