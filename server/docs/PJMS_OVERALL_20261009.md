# 2026-10-09 登录初始化缺失 7842 响应 · 1.2.3

## 用户现象与日志

用户使用 1.2.2 后仍停留在引导后的登录流程。保留第三次日志为 `evidence/lobby/user-client-03.jsonl`、`user-output-03.txt` 和 `user-launcher-game-03.jsonl`。
本次 219 条 game.command 中 218 条 Handled、1 条 Unsupported，没有服务端 error 事件，客户端未再记录上一版的四组异常。
唯一未响应请求为 7842，长度 2，时间为北京时间 2026-10-09 01:47:29；会话保持 Authenticated，之后持续心跳。
这确认了一个初始化响应缺口；实际大厅是否还有其他等待条件，仍由用户实机复验。

## 协议来源与静态证据

本机 9.1 的 UserAssembly.dll 和 global-metadata.dat 提供命令映射、类型名、字段名和序列化代码。未启动、附加调试或修改游戏。
旧参考 KianaProto.dll 没有这组消息，因此新增独立 `protocol/lobby91.proto`、`lobby91.desc` 及生成的 `Lobby91.cs`，保留原 lobby.desc 的来源。

| 证据 | 观察 |
| --- | --- |
| 命令表 RVA 0x0bf53e70 / 0x0bf53f03 | 7842 槽指向类型标记 0x3fc640，7843 槽指向 0x3fc660 |
| 类型记录索引 15345 / 15347 | 解码为 PjmsGetOverallReq / PjmsGetOverallRsp |
| 请求写入 RVA 0x0f1f8f30、读取 0x0f1f9220 | tag 1 为 repeated uint32 overall_id_list；tag 2 为 bool is_all |
| 响应写入 RVA 0x0f1f9f60、读取 0x0f1fa5a0 | tag 1 为 retcode；tag 2 为 repeated PjmsOverall；tag 3 为 bool is_all |
| 元素写入 RVA 0x0f1fa3d0 | tag 1 为 uint32 overall_id；tag 2 为 uint32 overall_value |
| 启动发送 RVA 0x07ff8f45 → 0x04719e70 | is_all=true，ID 列表为空，写入结果为 10 01 |

请求 `10 01` 由静态代码推导；用户日志只记录长度 2，未记录原始请求字节。
成功全量空快照为 `08 00 18 01`：显式 retcode=SUCC(0)、无 overall_list 元素、is_all=true。
静态恢复脚本为 `evidence/lobby/recover_pjms_overall.py`；结果、源文件 SHA256 和反汇编保存在 `pjms-overall-recovery.json`、`pjms-overall-verified.asm.txt`。脚本复现类型和字段解码，wire-type/字段号由上述序列化分支核对。

## 服务器实现

`LobbyHandlers.CreateStartupHandlers` 显式注册 7842 → 7843，使用独立生成消息解析请求；会话仍需通过认证。
当前没有 PJMS overall 进度，响应返回空列表；`PjmsGetOverallRsp.IsAll` 回传请求的 IsAll，全量与指定 ID 的增量查询分开处理。
不创建章节进度或奖励。其他未知命令保持 Unsupported。
新增一组初始化处理后，共 129 种原先缺失请求得到处理，注册入口总计 156 个。

## 验证结果

| 验证 | 结果与证据 |
| --- | --- |
| Release 分层测试 | 50 项通过，0 警告、0 错误；tests-client03.txt |
| 旧 1.2.2 对照 | 全量、指定 ID、64 位 KCP 全量三种请求均无响应；overall-baseline-122.json |
| 发布版 1.2.3 | 141 项原生 KCP 检查通过；三种 overall 查询均收到 7843，快照标志正确；published-client03.json |
| 之前四项结构修复 | 名片槽位、补给枚举存在性、BossInfo、MechaDefense 均通过 |
| 异地解压的一体包 | 登录器 9 项及整包 5 项检查通过；relocated-package-123.json |
| 本机迁移 | 1.2.2 设置、已有本地证书及 SQLite 数据复制到 1.2.3；全部数据表记录相同，integrity_check=ok；migration-123.json |

原生报告的 `passed` 表示核心检查，`client_contracts` 和 `overall_contracts` 保存专项结果；交付脚本要求新版全部通过，旧版三项 overall 均失败。
所有进程验证使用临时数据库和随机端口，没有操作实机游戏或改动系统代理/证书信任。
`gameplayReady=false` 保留；以上验证说明缺失请求已获得结构正确的回包，实际大厅由用户确认。

## 发布与交付

本机包：`dist/win-x64-1.2.3/`；干净分发包：`dist/win-x64-1.2.3.zip`。
`start-desktop.bat` / `server/start-game.bat` 现在打开 1.2.3 登录器，同包服务端自动托管。
原 1.2.2 发布目录、设置和存档未改写；干净 ZIP 不含个人设置、私有证书或玩家库。

四角色继续复用 `evidence/lobby/MODIFIED_FILE.zip`、`DIFF_FILE.patch`、`VERIFICATION.txt`、`ROLLBACK.sh`；1.2.2 的原角色及脚本已存于 `revisions/1.2.2/`。
原始 BASELINE.zip 的 SHA256 仍为 `31d9788af236920df5765228c56d7c4343a568285a4142b8dae6392eff6337b6`。
同一源码探针的 BASELINE / MODIFIED / ROLLBACK 分别观察到初始化处理数 0 / 129 / 0，回滚副本哈希等于原始基线；源码观察与实际网络验证分别记录。
回滚只作用于另一个源码 ZIP 副本，不会覆盖正在使用的工程或玩家数据。

## 后续验收

由用户关闭旧版并通过 1.2.3 登录器重新登录、跳过视频、验证大厅及重登数据。
若仍停留，读取 1.2.3 的 Server/logs 与最新 output_log.txt，确认 7842 → 7843 后的下一请求或客户端异常；保留已有修复与证据。
