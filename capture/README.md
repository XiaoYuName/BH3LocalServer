# 崩坏3 抓包助手

原生 WinForms / .NET 10 / Windows x64，独立单文件 `BH3Capture.exe`。入口为项目根目录 `start-capture.bat`，发布目录 `dist/BH3Capture-1.0.0-win-x64-single/`。工具不依赖登录器、服务端、Python或额外网络驱动。

界面、连续分段、Windows pktmon 生命周期及异常恢复参考并复用 `D:/AssetsStudio/DNF_115/Tools/PacketCapture/`；来源文件哈希见 [REFERENCE.json](REFERENCE.json)。在本工程独立维护，未修改115CN。

## 使用

1. 退出游戏及本地登录器，让本地代理恢复。
2. 运行 `start-capture.bat`，点“开始抓包”并接受Windows管理员授权。
3. 看到采集已启动后，通过官方启动器登录自己的账号。
4. 打开女武神、常用角色技能、武器、圣痕和编队页面，等待各页加载完成。
5. 点“结束并生成分享包”；通过“查看文件”或“更多操作 → 查看账号副本”找到结果。

默认保存到 `文档/BH3抓包/capture-时间-编号/`，每次创建新目录。完整说明已内置于界面，也可查看 [使用说明](使用说明.txt)。实机登录由用户操作。

## 输出

| 文件 | 内容 |
| --- | --- |
| `capture*.etl` | Windows原始UDP采集分卷 |
| `capture-*.pcapng` | 转换后的完整包长网络分卷 |
| `session.json` | 采集状态、案例、文件引用和基础检查结果 |
| `account-copy.json` | 按连接和UID保存的游戏数据响应；原始protobuf Base64及兼容模型JSON |
| `account-summary.txt` | 已捕获角色/武器/圣痕数量及缺项 |
| `check.json` / `check.txt` | KCP重组与核心快照检查，不写账号响应正文 |

支持响应11、25、27、48、507、602、644、2101。女武神技能、装备关联位于25；武器和圣痕等级、词条位于27。逐条保留响应顺序，不把增量列表拼成未经确认的完整账号；未知字段仍保留于原始protobuf。
副本导出排除登录请求、认证响应和令牌消息；原始网络分卷仍可能包含会话信息，仅留本机，程序没有上传功能。默认全部UDP，支持指定端口；不会设置代理、向官方发请求或注入游戏。

## 验证边界

KCP支持32位小端和BH3 64位大端conversation；按方向、连接及conversation重组，处理乱序、重复、冲突、分片和跨卷。解析Ethernet/VLAN、RAW/Loopback、IPv4与基础IPv6 UDP。
握手、序号缺口、截断、IP分片、冲突或资源上限均导致不完整提示。官方若有尚未适配的载荷封装，保留原始分卷并提示未识别，不能把采集完成称为账号完整复制。
核心快照完整仅表示基础数据、女武神、装备均有成功的全量响应且传输检查通过，不代表官方后台全量数据。现有protobuf为项目兼容模型，9.1差异需用本次实样核对。

结束采集自动导出账号文件；不会自动覆盖本地玩家库。本地服务端1.3.9已支持经复核的多角色、技能、武器与圣痕离线导入，并保留本地余额和进度；见 `../server/docs/ACCOUNT_IMPORT_20261009.md`。导入操作仍不会由抓包助手自动执行。

## 构建与检查

```powershell
dotnet run --project Capture.Check/Capture.Check.csproj -c Release
./publish.ps1
```

发布脚本先执行隔离检查，然后生成自带运行时的EXE及仅含该EXE的ZIP，拒绝覆盖既有输出。
64项检查覆盖传输、账号副本、独立分卷、停止归属、异常恢复和分段事务；7项发布包检查用独立编码的合成数据验证异地解压后的EXE；原生界面8种状态×3种尺寸预览通过。记录位于 `../server/evidence/lobby/capture-*.txt/json`。
真实pktmon、UAC及官方登录尚待用户操作验证，没有启动游戏或实际网络采集。

单文件也支持离线分析，无需SDK；输出目录必须不存在：

```text
BH3Capture.exe --inspect <pcapng文件或完整分卷目录> --output <新的输出目录>
```

返回码0表示核心快照满足检查，2表示已输出但不完整，1表示操作失败。GUI历史记录支持重新分析和修复转换。
