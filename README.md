# BH3 单机登录器（极简）

对照 Carol_Start / KianaBH 的做法：BH3 的 dispatch 是 HTTPS，不能像 DNF 那样只改 hosts。

本登录器做三件事：

1. 在 `127.0.0.1:20100` 起一个本地 SDK / dispatch 桩服务
2. 在 `127.0.0.1:8080` 起 MITM 代理，把 `.bh3.com` / `.mihoyo.com` 等登录相关请求转到桩服务
3. 可选接管 Windows 系统代理，然后启动 `Honkai Impact 3rd Game/BH3.exe`

## 启动

双击 `start.bat`，或：

```bat
C:\Users\Lumino Game 04\.workbuddy-ai\binaries\python\envs\default\Scripts\python.exe launcher.py
```

浏览器会打开 `http://127.0.0.1:17890/`。

第一次：点「安装根证书」→ 一键启动本地链路 → 启动游戏。

退出登录器时会自动停代理并还原系统代理。

## 还缺什么

- 现在只有 HTTP/SDK 桩，没有 KCP 游戏服（21000）。登录后进不了大厅是预期。
- 客户端是 9.1.0，dispatch AES 密钥先用元数据候选串。如果 `query_dispatch` 解不开，再补 9.1 密钥。
