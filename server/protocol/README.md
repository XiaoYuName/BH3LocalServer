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
