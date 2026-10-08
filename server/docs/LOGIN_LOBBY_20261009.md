# 2026-10-09 登录与大厅初始化修复

本页保留 1.2.1 阶段记录。最新进展见 [1.2.2 客户端异常修复](CLIENT_INIT_ERRORS_20261009.md)。

## 当前结果

用户使用 1.2.0 实机测试后，服务端日志确认 9.1 已通过 4→5、6→7 认证，KCP 格式为 `Bh3BigEndian64`。随后出现 123 种未处理初始化请求；客户端继续心跳和同步时间，但未进入大厅。

1.2.1 新增 122 个有参考定义的处理入口，总计 149 个注册命令（含心跳）。修复好友完整列表、邮件分页结束、请求选择字段回传、宿舍显示引用、商店及各活动初始化。`FinishGuideReportReq` 合并保存引导进度，拒绝无效或超量数据，不更改其他账号。

命令 **7842** 在工作区参考协议与已核对的公开参考中均无定义，仍记录 `Unsupported`，不猜测响应号。即使已知请求都得到回复，也不能认定真实大厅已可进入。

## 实现入口

| 层 | 内容 |
| --- | --- |
| Protocol | HMAC 本地身份票据、游戏分帧、protobuf 消息、显式命令号 |
| Game | `LobbyHandlers` 认证和基础数据；`StartupHandlers` 122 个初始化入口 |
| Persistence | schema 2；`player_lobby`、`client_data`；引导原子合并、按 UID 隔离 |
| Server | KCP 重组/分片/重传、会话管理、响应号及收发正文长度日志 |
| Launcher | 原生 Windows 登录器，自动托管同包服务端；密钥仅经子进程环境传递 |

空集合表示本地没有对应数据，携带完整列表标记。未开放的单个活动使用协议定义的关闭返回码；没有实现任务进度、悬赏刷新或外部网页授权时明确返回失败。没有通用“未知命令成功”兜底。
宿舍只关联已有初始角色，未解锁参考服的全部资产。战斗、奖励、抽卡、联机聊天仍未实现。

## 协议依据

消息来自用户工作区 `KianaProto.dll`，内部 descriptor 名为 `SeaRelWin8.4.0.proto`。本次导出 680 个消息和 43 个顶层枚举，保存于 `protocol/lobby.desc`。这些是 9.1 兼容定义，未声称已提取完整 9.1 协议。
实机日志证实认证和大端 64 位 KCP 链路可用；其他消息字段仍需用户下一次实机运行检验。

原生测试调用客户端 `kcp.dll`，SHA-256 为 `a926781a485ff68ff3e865b4cafc0b585439ce9956a6a5b0d124f6c628127f5c`。
其 `ikcp_input` 包含 offset 参数：`(kcp, buffer, offset, length)`。测试中的 64 位模式通过适配原生 32 位报文验证；用户日志独立确认真实客户端采用 64 位模式。

## 验证记录

- `evidence/lobby/user-client-01.jsonl`：用户 1.2.0 实机运行日志的保留副本。
- `missing-startup-01.json`：123 种缺失请求；`startup-coverage.json`：122 个已知请求的响应映射和处理策略。
- `tests-startup-01.txt`：44 项通过（Protocol 12、Game 10、Persistence 8、Server 14），Release 构建无警告、无错误。
- `native-startup-01.json`：134 项原生 KCP 进程检查通过，包括 122 个初始化响应、认证、分片、跨账号隔离和重启恢复。
- `published-startup-final.json`、`relocated-package-121-final.json`：新版发布服务和异地解压整包检查，134 项及 14 项全部通过。
- `selector-regression.txt`：补充非零 ChapterGroupId 回传检查后，Game 10 项复测通过。
- `build-startup-01.txt`、`build-startup-02.txt`：开发时枚举类型引用错误的原始输出，已修正；未覆盖失败记录。

请求日志仅保存命令号、状态、正文长度和响应号，不记录票据及完整正文。因此初始化回放使用相同消息号和默认请求；非默认选择字段另有单元测试。它不是原始字节流的完整回放，也不代替客户端界面验收。
原始 1.1 基线、修改源码包、二进制补丁、验证台账及回滚脚本沿用 `evidence/lobby/` 的四角色结构。回滚仅在隔离源码 ZIP 副本执行，不回滚玩家数据库。

## 本机发布与用户验收

旧登录器仍运行时发布至 `dist/win-x64-1.2.1/`，保持原目录可恢复。关闭旧登录器后使用新目录的 `Start.bat` 或 `Launcher/BH3.Launcher.exe`；仍由登录器自动启动服务端。
干净分发包为 `dist/win-x64-1.2.1.zip`，不含本机配置、账号或存档。已迁移本机数据的目录仅供本机使用。

实机启动、登录和大厅验收由用户进行；助手本轮只执行代码、构建、隔离自动化验证和日志分析。
下一步检查新版 `Server/logs` 中是否仍有 `Unsupported`、`session.error`，并结合客户端 `output_log.txt` 确认等待点。只有用户实际进入大厅并成功重登后，才记录大厅已通过验收；`gameplayReady` 继续保持 false。
