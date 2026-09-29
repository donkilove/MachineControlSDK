# MachineControlSDK

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

基于 .NET 10 的 XYZ 三轴机台串口控制库：A/B 区移动指令序列、ACK 判定与移动执行时序（9600 8N1，AT+IO 协议）。

本库从 [BurnMachineHost](https://github.com/donkilove/BurnMachineHost) 机台烧录上位机中提取并发布为可复用组件，让多个上位机应用可以共享一套经过充分验证的机台控制通信栈，而无需重复维护源代码。

## 功能特性

- **指令序列** —— A 区（`AT+IO=00` → `AT+IO=01`）/ B 区（`AT+IO=00` → `AT+IO=08`）移动指令序列
- **ACK 判定** —— trim 后大小写不敏感等于 `ok`（与原版 `response.lower() == "ok"` 一致）
- **移动执行器 `MachineWorker`** —— 逐条指令发送 + ACK 等待（空闲 2.5s 滑动窗口 + 帧尾 300ms + 4s 总预算：分片迟到可等齐、无换行回复帧尾快速判定；**分片间隔上限约 300ms**），成功后等待到位时间，整轮最多尝试 2 次（含 1s 重试间隔），协作式取消
- **回复长度上限** —— 256 字符，防畸形/恶意长帧
- **可注入串口通道** —— `ISerialChannel` 抽象 + `SerialPortChannel`（System.IO.Ports）真实实现 + `MockSerialChannel` 可编程模拟（离线开发/测试）

## 安装

```bash
dotnet add package MachineControlSDK --version 0.4.0 \
  --source "https://nuget.pkg.github.com/donkilove/index.json"
```

> GitHub Packages 源需要认证，请配置具有 `read:packages` 权限的令牌。

## 快速开始

```csharp
using MachineControl;
using MachineControl.Channel;

var worker = new MachineWorker(() => new SerialPortChannel(), Console.WriteLine);
var ok = await worker.MoveToAreaAsync(
    new MoveRequest("COM4", IsAreaA: true, SettleSeconds: 2),
    CancellationToken.None);

Console.WriteLine(ok ? "已移动到 A 区" : "移动失败");
```

无硬件环境可用 `MockSerialChannel` 离线开发：

```csharp
var mock = new MockSerialChannel();
mock.EnqueueResponse("ok\r\n");
mock.EnqueueResponse("ok\r\n");
var worker = new MachineWorker(() => mock);
```

> `SettleSeconds` 由调用方按行程类型传入：进区/复位用 `move_time_enter`，区内位间用 `move_time_between`。

## 协议摘要

| 指令 | 含义 |
|---|---|
| `AT+IO=00` | 复位/回零（两区序列的第一步） |
| `AT+IO=01` | 移动到 A 区（序列第二步） |
| `AT+IO=08` | 移动到 B 区（序列第二步） |
| `ok` | 成功 ACK（大小写不敏感） |

### 重试语义与固件行为

- **重试语义**：移动执行整轮最多尝试 2 次（含 1s 重试间隔）；**任一步失败即整轮从头重发**整个序列（`AT+IO=00` 复位指令会再次发送）。
- **固件行为（经固件方确认，2026-08-28）**：机台在移动/运动状态下**忽略新到达的指令**（含重复的 `AT+IO=00` 复位指令）——整轮重发不会造成「运动中重复复位」的物理动作重放；机台忙时重试的指令被忽略属预期行为。
- **重复执行边界**：ACK 在等待预算内（空闲 2.5s + 帧尾 300ms + 总预算 4s）未按时到达会被判失败并重发指令（整轮从头重发，见上）；若指令实际已被机台执行（仅响应迟到），重发会导致指令重复执行（at-least-once 语义），移动指令的重复执行是否无害同样依赖固件幂等。

完整协议规格见 BurnMachineHost 仓库 `docs/串口协议规格.md` §3。

## 构建与测试

```bash
dotnet build MachineControl.sln
dotnet test MachineControl.sln
```

## 版本历史

- **v0.3.2**（2026-08-29）—— 复审修复批次：解码缓冲溢出防护（1 字节输入可产 2 字符）、解码状态随会话重置（Open/ResetInputBuffer）、Mock 延迟响应到期项清除、Mock IsOpen 语义对齐真实通道（60 测试全绿）
- **v0.3.1**（2026-08-29）—— 审计修复批次（MC-01~08）：宿主状态回调异常隔离（杜绝物理动作重放）、Mock ResetInputBuffer 对齐 DiscardInBuffer、Open 失败路径释放实例、跨批次 UTF-8 解码、Read 超时不误判机台错误、ACK 等待改滑动窗口（分片迟到可等齐）+ 帧尾快速判定（无换行回复不等满空闲）、MachineSerial 入口校验（53 测试全绿）
- **v0.3.0**（2026-08-24）—— 升级 .NET 8 → .NET 10（TFM/CI setup-dotnet 10.0.x/依赖）；净版 net10 行为一致（31 测试全绿）

## 项目结构

```
MachineControl.sln
src/MachineControl/            类库（net10.0，NuGet 包 MachineControl）
├── MachineProtocol.cs         AT+IO 指令序列与 ACK 判定
├── MoveRequest.cs             移动请求（串口、区域、到位等待秒数）
├── MachineWorker.cs           移动执行时序
└── Channel/                   串口通道
    ├── ISerialChannel.cs      通道抽象（可注入自定义实现）
    ├── SerialPortChannel.cs   System.IO.Ports 实现
    └── MockSerialChannel.cs   可编程模拟通道
tests/MachineControl.Tests/    协议 + 执行器测试（58 个）
```

## 许可协议

[MIT](LICENSE)
