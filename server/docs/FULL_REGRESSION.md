# 完整回归与发布验证

## 使用

开发环境为 .NET SDK 10.0.401。`test.bat` 先构建解决方案，再依次运行四个 xUnit v3 可执行测试工程。
任意层失败都会返回非零退出码；不依赖 IDE，也不把测试放进服务器启动分支。

```bat
test.bat
publish.bat
python tools\smoke_published.py --exe dist\win-x64\Server\BH3.Server.exe --report evidence\skeleton\published-smoke.json
```

Python 3 只用于最后的发布进程探针；正式服务和登录器不依赖 Python。

## 分层范围

| 工程 | 验证范围 |
| --- | --- |
| Protocol.Tests | 握手字节序、精确长度、完整消息头尾、截断/溢出、未知字段往返 |
| Game.Tests | 重复握手、端点容量、单调时钟超时、身份生命周期、消息注册/状态门禁 |
| Persistence.Tests | schema 幂等、外键、更高版本/其他库拒绝、CAS、失败事务、重新打开 |
| Server.Tests | 配置预检、真实 HTTP/UDP、未知 UDP 不应答、回收、端口冲突清理、库锁 |

## 隔离与发布进程

测试数据位于系统临时目录 `bh3-server-test-*`，测试结束清理其自身目录。
发布探针使用 `bh3-published-smoke-*`，保留配置、日志和数据库便于复查，路径写入 JSON 报告。
它运行真实发布 EXE，核对只读预检、ready 能力、UDP 重复握手、stdin stop、EOF、重启存档和端口释放。
重启档案是探针显式写入临时库的 fixture，不代表游戏客户端登录成功。

## 结果判定

构建要求 0 警告、0 错误。测试要求 Errors/Failed/Skipped/Not Run 均为 0。
发布探针每项必须通过，并记录实际进程命令、stdout、stderr、退出码和 EXE SHA-256。
`hostReady` 与 `gameplayReady` 分开检查；后者在本阶段必须为 false。
真实客户端没有参与本轮，实机栏保持未验收。

## 证据

本轮原始输出在 `evidence/skeleton/test-output.txt`、`publish-output.txt` 与 `published-smoke.json`。
初次测试中的分析器格式问题、Windows 路径断言问题分别保留在 `attempt-01-analyzer.txt` 和 `attempt-02-path-assertion.txt`，修正后以最终输出为准。
发布探针发现 Console 同步读取器在返回 Task 前可能阻塞启动，已将 stdin 观察移入独立任务；失败输出保留为 `attempt-03-stdin-startup.txt`，之后重新验证 stop 与 EOF。

## 一体发布回归

运行 `python tools/verify_bundle.py --archive dist/BH3-Local-1.1.0-win-x64.zip --report evidence/bundle/relocated-package.json`，在异地解压目录执行登录器 9 项检查和 5 项服务端托管检查。默认使用临时数据与随机端口。
