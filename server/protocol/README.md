# 登录与大厅协议来源

`lobby.desc` 是 protobuf FileDescriptorSet，包含本轮使用的 711 个消息和 44 个顶层枚举；`src/BH3.Protocol/Generated/Lobby.cs` 由 protoc 生成。
原始来源为用户工作区 `LocalServer/千乐铃音_7.0/KianaBH/KianaProto.dll`，内部文件名为 `SeaRelWin8.4.0.proto`。
协议用于本地 9.1 兼容开发，尚待用户实机验证；未声明其来自 9.1 客户端提取。

生成工具：NuGet `Grpc.Tools 2.69.0` 中的 protoc。
在 server 目录运行：

```text
protoc --descriptor_set_in=protocol/lobby.desc --csharp_out=src/BH3.Protocol/Generated lobby.proto
```

选择与依赖递归导出工具、命令号及字段检查保存在 `evidence/lobby/reference/SchemaExport`。
源文件、客户端 KCP 库及初始配置参考数据的哈希见 `sources.json`。

已知初始化消息 129 对来自用户启动日志。认证及 KCP 格式已有用户实机日志支持，完整大厅仍待复验。

`lobby91.proto` / `lobby91.desc` / `Generated/Lobby91.cs` 独立维护本机 9.1 静态确认的增量：
`PjmsGetOverallReq=7842`、`PjmsGetOverallRsp=7843` 及 `PjmsOverall`。
类型名、字段名、命令映射与 wire-type 均有本机静态证据，详见 [7842 专项](../docs/PJMS_OVERALL_20261009.md)。它们不来自旧版参考 DLL；重新导出 lobby.desc 时不得覆盖此增量。

```text
protoc --proto_path=protocol --csharp_out=src/BH3.Protocol/Generated --descriptor_set_out=protocol/lobby91.desc lobby91.proto
```

9.1 实机解码器不接受 GachaType=0，不能显式编码参考协议自动补出的 NONE；全量请求省略 type 字段。

`settlement.proto` 增补结算辅助请求。1.3.7 的 `PjmsGetCurWorldRsp.world=2` 复用 `lobby.desc` 中的 `PjmsWorld`，生成时提供依赖描述符：

```text
protoc --descriptor_set_in=protocol/lobby.desc --proto_path=protocol --csharp_out=src/BH3.Protocol/Generated --descriptor_set_out=protocol/settlement.desc settlement.proto
```

1.4.0 增加邮件 3802–3810 与补给 4700–4709 依赖，当前 lobby.desc 含 779 个消息和 49 个顶层枚举。当前抓包验证记录见 GM_20261009.md；3804/3805 的参考类型名为 MarkReadClientMailReq/Rsp，命令枚举名为 MarkClientMailReadReq/Rsp。

1.5.2：`lobby.desc` 保留已有修订并合入 ELF 养成消息；MissionStatus 的 Doing/Finish 按 9.1 改为 2/3，ElfSkill 增加字段 3 is_mask。`companion91.proto` 为 1742/1743 技能开关，单独通过 protoc 生成。生成工具 `tools/UpgradeCompanionProtocol` 接收当前 descriptor 与参考新增 descriptor 两个路径。静态证据与边界见 `docs/COMPANIONS_SUPPLY_20261009.md`。

1.6.0：新增251/252消耗品、753—766神之键变更、1195/1196收藏、288/289/969作战历练、3750—3768通行证、4321/4322活动任务组；活动配置位于111字段50。保留9.1 MissionStatus及ElfSkill增量。参考模式仍非完整9.1赛季配置，见SYSTEMS专项。
