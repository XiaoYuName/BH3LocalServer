# BH3 9.1 服务端

Windows x64 / .NET 10，面向崩坏 3 PC 国服 9.1.0 的独立游戏服务器工程。
[当前状态](docs/STATUS.md)记录实现与验收范围，[文档索引](docs/README.md)提供开发入口。

工程按 Protocol、Game、Persistence、Server 四层维护，支持独立构建、测试、发布和存档。
1.3.0 在已实机通过的大厅基础上，新增第一章普通主线关卡、进入、通关结算、任务/星级领奖与 SQLite schema 3 事务持久化。
真实 9.1 大厅已由用户验收；副本画面待用户验收。当前协议属于兼容候选，`gameplayReady` 保持 `false`。详见 [登录与大厅专项](docs/LOGIN_LOBBY_20261009.md)。

新版独立发布于 `dist/win-x64-1.3.0/`，关闭旧版后打开新版 `Launcher/BH3.Launcher.exe`。

日常使用 [启动登录器](start-game.bat)，点击“启动服务”即可自动托管同包服务端。完整包说明见 [一体发布](docs/BUNDLE_RELEASE_20261009.md)。

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
