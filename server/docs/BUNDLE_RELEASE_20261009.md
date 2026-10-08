# 2026-10-09 登录器与服务端一体发布

## 结果

原发布脚本只生成游戏服务器，用户还要手工选择外部服务端。本轮统一为 115CN 风格的 `dist/win-x64/Launcher` 和 `Server` 两个目录。
日常从 `start-game.bat`、项目 `start-desktop.bat`、games 目录 `启动崩坏3.bat` 或包内 `Start.bat` 打开同一个登录器。
无需手动运行服务端，也无需填写服务端路径。点击“启动服务”会托管同包服务器；点击“启动游戏”会自动准备服务。

## 接线

`LauncherConfig.ResolveServerExe` 在未配置自定义路径时按登录器相对位置定位 `../Server/BH3.Server.exe`。
包标识 `BH3.package.json` 存在但服务端丢失时明确报错，不悄悄回退到旧握手桩。
`LauncherEngine.Start` 传入 `--launcher-control`、配置文件和 `--game-port`，轮询健康接口，核对服务名称、子进程 PID、UDP 端口与 hostReady。
就绪后才启动本地 SDK/代理。停止时通过 stdin 正常停服；服务端异常退出会自动停止本地链路。
启动任何环节失败都回收本次创建的进程和监听。

## 发布与迁移

`server/publish.bat` 和 `desktop/publish.bat` 均生成完整包。
首次本地发布复制旧 `server/dist/server/config`、`data` 和 `desktop/dist/Data/settings.json` 到新布局，保留旧目录。
后续发布保留已有目标配置、设置与玩家存档。自定义服务端路径保持用户选择；旧默认服务端路径在迁移副本中转为空以启用自动定位。
`BH3-Local-1.1.0-win-x64.zip` 从全新暂存构建生成，早于本机数据迁移，因此不含个人设置、日志或玩家档案。

## 验证

- 服务端 33 项回归通过。
- 发布登录器 9 项基础检查通过。
- 5 项整包检查通过：自动定位、真实子进程健康/端口、正常停止、异常退出清理和端口冲突回滚。
- ZIP 解压至系统临时目录后，以上 9+5 项再次通过；输入位置与原始输出记录在 `evidence/bundle/relocated-package.json`。

最初测试输出按 UTF-8 读取 Windows 控制台编码失败，测试程序本身退出 0、JSON 报告通过。验证脚本已按实际输出编码读取，保留原记录并完成重新取证。
未启动真实客户端，未安装证书或修改真实代理。本轮修复的是发布与托管体验，KCP、游戏认证和大厅仍未接通。
