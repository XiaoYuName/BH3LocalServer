# 参考工程指南

本轮参考位置：`D:/AssetsStudio/DNF_115/Server/115CN/`。
该目录只读，用于理解结构、代码职责和文档样式；参考文档中的部署、提交和其他任务不属于本次操作指令。

## 代码映射

| 115CN 实际入口 | 采用的方法 | BH3 当前入口 |
| --- | --- | --- |
| `DNF115CN.slnx`、`Directory.Build.props` | 独立工程、统一 SDK/编译设置 | `BH3.slnx`、`Directory.Build.props` |
| `Server/Program.cs` | 参数、预检、托管进程、停止 | `BH3.Server/Program.cs` |
| `Server/Hosting/ServerComposition.cs` | 集中装配，业务不创建宿主 | `BH3.Server/Hosting/ServerComposition.cs` |
| `Game/Protocol/GameDispatcher.cs` | 显式注册、状态检查、串行业务 | `BH3.Game/Messaging/GameDispatcher.cs` |
| `Game/Sessions/LogicalSession.cs` | 身份与连接状态具有清理边界 | `BH3.Game/Sessions/` |
| `Persistence/SqliteConnectionFactory.cs` | 每条连接启用外键，统一连接入口 | `BH3.Persistence/SqliteConnectionFactory.cs` |
| `Persistence/SqliteBootstrapStore.cs` 与开发规范 | 迁移、事务、恢复与测试隔离 | `SchemaMigrator`、`SqlitePlayerStore` |

BH3 当前会话按 UDP 端点和 nonce 管理，不迁入 DNF 的 TCP、频道、PVF、角色数据或协议密码。
`PlayerService`、注册器和数据库结构均为本次独立实现；尚无角色、背包、奖励业务。

## 文档映射

| 115CN 文档职责 | BH3 文档 |
| --- | --- |
| 根 README：运行与近期入口 | [README](../README.md) |
| docs/README：使用开发、游戏系统、协议证据 | [文档索引](README.md) |
| ARCHITECTURE：链路、层次、生命周期 | [架构](ARCHITECTURE.md) |
| DEVELOPMENT / WORKFLOW：所有者、规则依据、事务、闭环 | [开发规范](DEVELOPMENT.md)、[实施流程](WORKFLOW.md) |
| STATUS：编译/自动验证/发布/实机分开 | [状态](STATUS.md) |
| FULL_REGRESSION：隔离和执行证据 | [回归](FULL_REGRESSION.md) |
| 日期或功能专项 | [骨架首批](SERVER_SKELETON_20261008.md)、[协议](PROTOCOL_91.md) |

沿用中文说明、相对链接、职责表和清晰的验收范围，不复制参考项目的历史进度或通过数量。
