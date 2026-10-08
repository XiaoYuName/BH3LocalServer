# 2026-10-09 登录舱门初始化全量标志修复 · 1.2.4

## 实机验收补记

用户于 2026-10-09 确认本版实机测试通过，已能进入大厅。后续继续实现出击主线与副本结算；以下保留修复和离线验收过程。

## 用户现象与本次证据

用户确认 1.2.3 停在登录舱门，画面不再推进。本次保留日志为 `evidence/lobby/user-client-04.jsonl`、`user-output-04.txt`。
201 条请求均为 Handled，7842 已收到四字节 7843，服务端无 error，客户端没有上一版的四组异常。不能再将本次停留归因于缺少 7843。

静态分析发现客户端的初始化模块仅在响应校验通过时删除待响应项。角色、装备等模块要求 retcode=0 且 IsAll=true。
当前服务端把“列表为空”视为全量，但本机 9.1 初始化发送器向 ID 列表追加的是 `0`。
因此旧版虽回复成功，却发出 IsAll=false；角色和装备还被错误过滤为空，无法满足这些初始化条件。

## 静态证据与请求输入

只读取本机 UserAssembly.dll 和 metadata，没有启动游戏、读取进程内存或修改客户端。
方法名称与 RVA 映射由 `client91_metadata.py` 恢复，专项复现脚本为 `recover_init_selectors.py`。
源文件哈希、类型字段及函数位置记录在 `init-selectors-recovery.json`，完整函数保存在 `init-selectors-verified.asm.txt`。

| 请求 / 响应 | 启动回调 → 发送函数 RVA | 输入 | 完成条件 |
| --- | --- | --- | --- |
| 24 / 25 角色 | 0x7e6cce0 → 0xa725960 | avatar_id_list=[0] | 成功且 IsAll |
| 26 / 27 装备 | 0x7e6cc20 → 0xa727160 | 四类 ID 列表各为 [0] | 成功且 IsAll |
| 449 / 450 勋章 | 0x4184010 → 0xa748340 | medal_id_list=[0] | 成功且 IsAll |
| 506 / 507 神之键 | 0x3db1ea0 → 0x4705e00 | key_id_list=[0] | 成功且 IsAll |
| 643 / 644 宿舍角色 | 0x7b3ea00 → 0xa74ce10 | avatar_id_list=[0] | 成功且 IsAll |
| 1197 / 1198 手机挂件 | 0x47f8740 → 0xa74fc00 | phone_pendant_id_list=[0] | 成功且 IsAll |

列表追加函数 RVA 0xda85dd0 将传入值写入 uint32 数组并递增数量。上述路径明确传入零；设备路径对四个列表分别执行。
基类 ProcessPacketForInit RVA 0x7701660 仅在校验返回 true 后移除等待项；IsInitFinish RVA 0x7701e70 要求待发送和待响应集合都为空。

测试字节由静态发送参数及现有 lobby.desc 字段号构成：单列表 `08 00`，装备 `08 00 10 00 18 00 20 00`。
用户日志只记录正文长度，以上不是实机原始抓包。整个客户端是否还有其他等待条件仍待用户验证。

## 实现范围

`LobbyHandlers.RequestsAll` 识别空列表或包含零的列表。六处响应使用该规则；装备仅在各类查询均为全量时标记整个快照完成。
角色和武器的全量请求返回该账号已有数据，单项请求保留过滤；混合装备查询不会把局部结果标记成全量。
勋章、神之键、宿舍角色成长和手机挂件当前无记录，返回成功空快照与正确的 IsAll。
没有伪造角色、装备、奖励或玩法进度。保留 1.2.2、1.2.3 的已验证修复，注册命令数量不变。

## 离线与隔离进程验证

| 检查 | 结果 / 证据 |
| --- | --- |
| 分层测试 | 52 项通过；tests-client04.txt |
| 旧版 1.2.3 网络对照 | 两种 KCP 格式下六类零 ID 全量查询共 12 项失败，12 项单项查询通过；selectors-baseline-123.json |
| 新版 1.2.4 同输入 | 上述 24 项全部通过，角色和装备各返回已有记录；published-client04.json |
| 原有网络检查 | 141 项 KCP、4 项客户端结构、3 项 overall 全部通过 |
| 干净一体包异地解压 | 登录器 9 项及托管服务 5 项通过；relocated-package-124.json |
| 设置与数据 | 1.2.3 配置、设置、已有本地证书及 SQLite 备份迁入 1.2.4，全部表记录一致且 integrity_check=ok；migration-124.json |

`verify_lobby.py --observe-init-selectors` 单独记录专项观察；总 passed 表示基础检查，验收还必须检查 init_selector_contracts、client_contracts、overall_contracts 全部通过。
首次旧版探针关闭 UDP 端点时出现 Windows 10054，原始失败报告保留为 selectors-baseline-123-attempt1.json/txt。探针已改为发送断开包并保留 UDP 端点至服务退出，重新执行同一请求后完成对照；本轮未修改生产 UDP 宿主。
所有验证使用临时库、随机端口；未操作实机、代理或系统证书信任。gameplayReady=false 保留。

## 发布、交付与后续验收

当前完整包为 `dist/win-x64-1.2.4/`，干净分发包为 `dist/win-x64-1.2.4.zip`。
`start-desktop.bat` / `server/start-game.bat` 已选择 1.2.4，登录器自动托管同包服务端。
旧目录与数据保留；干净 ZIP 在迁移之前生成，不含个人设置、私有证书和存档。

四角色继续使用 `evidence/lobby/MODIFIED_FILE.zip`、`DIFF_FILE.patch`、`VERIFICATION.txt`、`ROLLBACK.sh`；1.2.3 原件及脚本归档至 revisions/1.2.3。
原始源码基线 SHA256 保持 `31d9788af236920df5765228c56d7c4343a568285a4142b8dae6392eff6337b6`。
相同源码探针观察 BASELINE / MODIFIED / ROLLBACK 初始化处理数为 0 / 129 / 0，零 ID 规则为无 / 有 / 无；回滚副本哈希与原始基线一致。
源码探针不等于运行验收，网络对照单独记录。ROLLBACK.sh 只恢复传入的源码 ZIP 副本，不覆盖工作区或玩家数据。

用户关闭旧登录器后通过新版入口登录，验证舱门推进、大厅及重登数据。如果仍卡住，读取新版日志和 output_log.txt，继续核对初始化模块与大厅转换条件。
