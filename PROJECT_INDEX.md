# BH3 当前项目入口

## 联调分工（用户于 2026-10-09 确认）

实机启动游戏、登录和大厅验证由用户进行。只有用户明确要求实机联调时，助手才操作游戏客户端；常规任务由助手完成代码、构建、隔离自动化测试、发布包与日志分析。

## 游戏服务器

[服务端说明](server/README.md) · [文档索引](server/docs/README.md) · [当前状态](server/docs/STATUS.md)

正式工程入口为 `server/BH3.slnx`，四层为 Protocol、Game、Persistence、Server。
已实现 KCP、账号票据与大厅基础数据。1.2.4 修正六类初始化零 ID 全量查询，恢复已有角色、装备与 IsAll 完成标志；52 项分层、141 项 KCP、24 项查询及既有专项通过。用户于 2026-10-09 确认实机大厅测试通过，详见 [零 ID 初始化专项](server/docs/INIT_SELECTORS_20261009.md)。下一阶段补齐出击主线、副本进入与通关结算。
日常运行 `start-desktop.bat` 或 `server/start-game.bat`。当前 1.2.4 一体包位于 `server/dist/win-x64-1.2.4/`，登录器自动托管同包服务端，无需选择路径。

## 原生登录器

[桌面版说明](desktop/README.md)，入口 `start-desktop.bat`。已接入验证过的 9.1 dispatch 密钥。
原根目录 README、Python 脚本和 web 目录保留为原型资料；后续游戏业务进入 server 工程。

## 本轮保留范围

原骨架证据位于 `server/evidence/skeleton/`；一体发布修订及原文件备份位于 `server/evidence/bundle/`。旧发布目录、配置和存档保留，新包自动迁移首次运行所需数据。

最新专项：[登录舱门初始化全量标志修复](server/docs/INIT_SELECTORS_20261009.md)。
