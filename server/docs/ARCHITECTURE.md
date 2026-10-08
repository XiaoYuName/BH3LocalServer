# 系统架构

面向 PC 国服 9.1.0。运行入口见 [服务端 README](../README.md)，验收范围见 [STATUS](STATUS.md)。
采用 115CN 的职责分层和组合根方式，BH3 的网络传输、报文和资源规则独立实现。

## 运行链

```text
WinForms Launcher
  -> 本地 SDK / dispatch + HTTPS 代理
  -> BH3.exe 9.1.0
  -> BH3.Server UDP 21000
       -> 20 字节握手 -> SessionRegistry
       -> KCP 重组 -> GamePacketCodec -> GameDispatcher
       -> 账号票据验证 / 大厅初始化用例
  -> PlayerService -> IPlayerStore / SQLite

运维读取 -> HTTP 21080 /health/live /health/ready /api/status
```

用户实机已确认认证链路；大厅初始化仍待复验，详细能力见登录专项。

## 分层

| 层 | 负责 | 不负责 |
| --- | --- | --- |
| 桌面登录器 | 客户端选择、SDK/dispatch、证书、代理与托管进程 | 游戏报文业务 |
| Protocol | 握手字节、完整消息分帧、边界验证和未知前缀保留 | Socket、账号认证、存档 |
| Game | 会话状态、消息注册/分发、玩家用例与身份归属 | 网络监听和具体宿主 |
| Persistence | SQLite 连接、schema、原子写入和修订号 CAS | 猜测命令号、发送响应 |
| Server | 配置、组合根、UDP/HTTP、进程生命周期和日志 | 角色、装备、关卡规则 |

依赖方向：`Server → Game → Protocol / Persistence`，Protocol 与 Persistence 互不引用。
`ServerComposition` 是组合根；Protocol 使用 Google.Protobuf，Persistence 使用 Microsoft.Data.Sqlite。

## 宿主与资源生命周期

`Program.Main` 解析参数；`ServerRuntime.StartAsync` 校验配置、锁定库文件、迁移、监听 UDP，再启动健康 HTTP。
任一步失败都释放已获得的监听与锁。UDP 接收或回收循环意外停止会使 ready 失败，主进程随后退出非零。
停止时先将 ready 置为 false，再停止 HTTP、取消 UDP、清理会话、释放存档锁和日志。

同一个数据库不能同时被两个宿主拥有。健康 HTTP 仅返回能力、会话数量、消息注册表和计数，无玩家数据写入入口。
`hostReady=true` 只表示宿主运行正常；完整玩法需要 `gameplayReady`，本阶段始终为 false。

## 会话与数据

连接按完整远端 IP:port 区分。相同 nonce 的重复握手复用 conv，新 nonce 关闭旧会话。
每个会话的分发与关闭通过同一锁串行执行。玩家身份只能来自认证后的会话，不读取报文声明 UID 作为授权依据。

玩家数据使用独立 SQLite 库；网络状态不存档。没有预置正式玩家，也没有默认登录成功处理器。
后续资源目录应按 BH3 客户端版本和内容指纹建立独立只读数据源，不引入 DNF PVF 结构。
