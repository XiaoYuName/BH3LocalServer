# 通关后空奖励弹窗 · 1.3.10

用户在 1.3.9 实机通关后出现空的“获得奖励”弹窗。10106、10109 的日志均为成功结算，每关基础金币 750、经验 6，挑战索引 0/1/2 已提交。最新存档含 6 条结算回执、5 个已通关关卡；金币 25750、水晶 105。奖励已经入账。

## 根因

`CampaignService.End` 无条件设置 `StageEndRsp.LineEnhanceRewardData = new RewardData()`，将不存在的额外奖励序列化成 field 19 的空子消息 `9a0100`。

9.1 客户端 `LevelResultDialogContext.OnStageEndRsp` 在 RVA `0x9268269` 保存回包到 `this+0x158`。`OnDropNewItemDialogsEnd` 在 `0x9277b5f` 只检查 `line_enhance_reward_data`（回包对象 `+0x20`）是否为 null；非 null 就在 `0x9277c30` 调用 `RewardGotDialogContext` 构造函数并加入弹窗队列。空子消息也满足条件，因此弹窗没有任何内容。实际客户端文件哈希、方法映射和反汇编见 `evidence/lobby/empty-reward-1310/static-proof.json`。

## 修复与验证

普通关卡不再创建该额外奖励对象。旧回执在响应时移除字节大小为零的同字段，保留回执原始数据、钱包、挑战星和正在进行的其他战斗。非空奖励与未知 protobuf 字段均不清除。此修复不补发奖励，也不把基础奖励重复塞入额外奖励弹窗。

8 项新回归在旧版有 6 项失败，修复后全部通过；32/64 位 KCP 与重启共 5 项新增字段检查在旧版失败、新版通过。92 项分层测试、341 项协议检查和 16 项异地整包检查通过。

1.3.10 复制当前 1.3.9 的最新存档，7 张表逐行一致，包含已导入的角色装备。原 1.3.9 数据未修改。`start-desktop.bat` 指向新版。用户重启登录器后再通关验证；助手未启动实际游戏，画面仍需实机复验。

四角色继续使用 `evidence/lobby/MODIFIED_FILE.zip`、`DIFF_FILE.patch`、`VERIFICATION.txt`、`ROLLBACK.sh`；1.3.9 原角色保存在 `revisions/1.3.9/`。本轮证据位于 `empty-reward-1310/`。
