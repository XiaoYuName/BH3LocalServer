# 原生登录器接入

## 日常启动

运行 `start-game.bat` 或包内 `Start.bat`，只打开原生登录器。发布目录同时包含 `Launcher/BH3.Launcher.exe` 与 `Server/BH3.Server.exe`，不需要手动运行服务端或选择其路径。
点击“启动服务”即可托管服务器和本地 SDK/代理；点击“启动游戏”也会自动准备服务。服务端已处理认证和大厅初始化，实际大厅由用户验收。

## 默认定位与配置

“自定义服务端”保持为空时按相对位置定位 `../Server/BH3.Server.exe`，整包移动后仍有效。只有要替换服务端时才使用该选项。
包标识存在但 Server 丢失时明确报错。运行时配置为 `Server/config/server.json`，数据为该配置中指定的路径，默认 `Server/data/bh3.db`。
登录器传入 `--transport kcp` 与 `--game-port`，以首页/设置中的游戏端口覆盖进程本次 UDP 端口，不改写服务端配置文件。

## 启动与停止

`LauncherEngine.Start` 创建隐藏的服务端进程，使用 `--launcher-control` 并重定向 stdin/stdout/stderr。
它等待 `/health/ready`，核对 `service=BH3.Server`、子进程 `processId`、`gamePort` 与 `hostReady`，通过后才显示链路就绪。
停止或关闭登录器时发送 `stop` 并关闭 stdin，服务端正常释放监听。服务端意外退出时登录器清理本地链路；端口冲突或启动失败同样回收本次子进程。

## 发布与迁移

`server/publish.bat` 和 `desktop/publish.bat` 都生成完整一体包。首次复制旧设置与存档到新布局，保留旧文件，后续发布保留已有配置和玩家库。
分发 ZIP 来自未迁入本机数据的干净构建；只需将整个压缩包发给使用者，保留 Launcher 与 Server 的相对位置。

## 能力和验证

健康接口就绪表示宿主运行正常，`gameplayReady=false` 保留，默认 `transport=kcp`。
5 项整包自动检查覆盖默认定位、真实子进程健康/UDP、停止、异常退出和冲突回滚；压缩包移动解压后也已验证。
详见 [一体发布专项](BUNDLE_RELEASE_20261009.md) 和 `evidence/bundle/relocated-package.json`。
