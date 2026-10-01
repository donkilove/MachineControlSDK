using MachineControl;
using MachineControl.Channel;
using Xunit;

namespace MachineControl.Tests;

/// <summary>
/// MachineWorker 集成验证：与协议层、串口通道抽象衔接
/// （对照 BurnMachineHost docs/测试计划.md §5 手工冒烟的可自动化部分）。
/// </summary>
public class MachineWorkerTests
{
    private const string MachineSerial = "COM4";
    private const double MoveTimeEnter = 0.01;
    private const double MoveTimeBetween = 0.01;

    private static MoveRequest NewRequest(bool isAreaA = true, double settleSeconds = MoveTimeEnter)
        => new(MachineSerial, isAreaA, settleSeconds);

    [Fact]
    public async Task MachineWorker_AreaA_AcksBothCommands()
    {
        var port = new MockSerialChannel();
        port.EnqueueResponse("ok\r\n");
        port.EnqueueResponse("OK");
        var statuses = new List<string>();
        var worker = new MachineWorker(() => port, statuses.Add);

        var ok = await worker.MoveToAreaAsync(NewRequest(), CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(2, port.Writes.Count);
        Assert.Equal("AT+IO=00\r\n", port.Writes[0]);
        Assert.Equal("AT+IO=01\r\n", port.Writes[1]);
        Assert.Contains(statuses, s => s.Contains("机台控制指令执行成功"));
    }

    [Fact]
    public async Task MachineWorker_AreaB_AcksBothCommands()
    {
        var port = new MockSerialChannel();
        port.EnqueueResponse("ok\r\n");
        port.EnqueueResponse("ok\r\n");
        var worker = new MachineWorker(() => port);

        var ok = await worker.MoveToAreaAsync(NewRequest(isAreaA: false, settleSeconds: MoveTimeBetween), CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(2, port.Writes.Count);
        Assert.Equal("AT+IO=00\r\n", port.Writes[0]);
        Assert.Equal("AT+IO=08\r\n", port.Writes[1]);
    }

    [Fact]
    public async Task MachineWorker_NonOkReply_Fails()
    {
        var port = new MockSerialChannel();
        port.EnqueueResponse("ERROR\r\n");   // 每条都失败 → 重试耗尽
        port.EnqueueResponse("ERROR\r\n");
        var worker = new MachineWorker(() => port);

        var ok = await worker.MoveToAreaAsync(NewRequest(), CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(2, port.Writes.Count);   // 两轮重试，每轮都因第一条 ACK 失败而中止
        Assert.All(port.Writes, w => Assert.Contains("AT+IO=00", w));
    }

    // ---- 审核修复：协议健壮性（对照串口协议规格 §3.3） ----

    [Fact]
    public async Task MachineWorker_AckFailure_RetriesAfterOneSecond()
    {
        var port = new MockSerialChannel();
        port.EnqueueResponse("ERROR\r\n");   // 两轮都失败
        port.EnqueueResponse("ERROR\r\n");
        var worker = new MachineWorker(() => port);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ok = await worker.MoveToAreaAsync(NewRequest(), CancellationToken.None);
        sw.Stop();

        Assert.False(ok);
        // 协议规格 §3.3：整轮最多 2 次，失败重试间隔 1 s（审核修复：ACK 失败路径此前无间隔）
        Assert.True(sw.ElapsedMilliseconds >= 900, $"ACK 失败重试应有约 1s 间隔，实际 {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task MachineWorker_OversizeAckReply_Fails()
    {
        var port = new MockSerialChannel();
        port.EnqueueResponse(new string('A', 300) + "\r\n");   // 回复长度超限（>256）→ 判失败
        port.EnqueueResponse("ERROR\r\n");
        var worker = new MachineWorker(() => port);

        var ok = await worker.MoveToAreaAsync(NewRequest(), CancellationToken.None);

        Assert.False(ok);
    }

    [Fact]
    public async Task MachineWorker_OpenFailure_RetriesThenFalse()
    {
        var port = new MockSerialChannel { OpenError = "拒绝访问" };
        var worker = new MachineWorker(() => port);

        var ok = await worker.MoveToAreaAsync(NewRequest(), CancellationToken.None);

        Assert.False(ok);
    }

    // ---- 审核补充：重试恢复路径（第一轮失败、第二轮成功） ----

    [Fact]
    public async Task MachineWorker_FirstRoundAckFails_SecondRoundSucceeds()
    {
        var port = new MockSerialChannel();
        port.EnqueueResponse("ERROR\r\n");   // 第一轮：第一条 ACK 失败 → 整轮重试
        port.EnqueueResponse("ok\r\n");      // 第二轮：全部成功
        port.EnqueueResponse("ok");
        var worker = new MachineWorker(() => port);

        var ok = await worker.MoveToAreaAsync(NewRequest(), CancellationToken.None);

        Assert.True(ok);
        // 第一轮：第一条 ACK 失败即中止（写 1 条）；第二轮成功（写 2 条）→ 共 3 条
        Assert.Equal(3, port.Writes.Count);
        Assert.Equal("AT+IO=00\r\n", port.Writes[0]);
        Assert.Equal("AT+IO=00\r\n", port.Writes[1]);
        Assert.Equal("AT+IO=01\r\n", port.Writes[2]);
    }

    // ---- 审核补充：取消契约（OCE 重抛 + 串口关闭，不得误判为失败重试） ----

    [Fact]
    public async Task MachineWorker_CancelDuringAckWait_ThrowsOperationCanceledAndClosesPort()
    {
        var port = new MockSerialChannel();
        port.EnqueueResponse("ok\r\n");   // 第一条 ACK 成功
        // 第二条无回复 → 2s ACK 窗口等待中取消
        var cts = new CancellationTokenSource();
        var worker = new MachineWorker(() => port);

        var task = worker.MoveToAreaAsync(NewRequest(), cts.Token);
        while (port.Writes.Count < 2)
        {
            await Task.Delay(10);
        }

        cts.Cancel();
        // Task.Delay 取消抛 TaskCanceledException（OCE 子类）：语义为取消，非失败重试
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.False(port.IsOpen);   // finally 中已关闭
    }

    [Fact]
    public async Task MachineWorker_CancelDuringRetryDelay_ThrowsOperationCanceledAndClosesPort()
    {
        var port = new MockSerialChannel();
        port.EnqueueResponse("ERROR\r\n");   // 第一轮失败 → 进入 1s 重试间隔
        var cts = new CancellationTokenSource();
        var worker = new MachineWorker(() => port);

        var task = worker.MoveToAreaAsync(NewRequest(), cts.Token);
        while (port.Writes.Count < 1)
        {
            await Task.Delay(10);
        }

        await Task.Delay(100);   // 等待 ACK 失败结算并进入重试间隔
        cts.Cancel();
        // Task.Delay 取消抛 TaskCanceledException（OCE 子类）：语义为取消，非失败重试
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.False(port.IsOpen);
    }

    [Fact]
    public async Task MachineWorker_CancelDuringSettleWait_ThrowsOperationCanceledAndClosesPort()
    {
        var port = new MockSerialChannel();
        port.EnqueueResponse("ok\r\n");
        port.EnqueueResponse("ok");
        var cts = new CancellationTokenSource();
        var worker = new MachineWorker(() => port);

        var task = worker.MoveToAreaAsync(NewRequest(settleSeconds: 30), cts.Token);
        while (port.Writes.Count < 2)
        {
            await Task.Delay(10);
        }

        await Task.Delay(50);   // 进入到位等待
        cts.Cancel();
        // Task.Delay 取消抛 TaskCanceledException（OCE 子类）：语义为取消，非失败重试
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.False(port.IsOpen);
    }

    // ---- 审核补充：ACK 窗口超时（无任何回复）路径 ----

    [Fact]
    public async Task MachineWorker_NoReply_TimesOutAndFails()
    {
        var port = new MockSerialChannel();   // 队列空 → ReadAvailable 恒为空 → 2s 窗口超时
        var statuses = new List<string>();
        var worker = new MachineWorker(() => port, statuses.Add);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ok = await worker.MoveToAreaAsync(NewRequest(), CancellationToken.None);
        sw.Stop();

        Assert.False(ok);
        // 两轮：每轮 2s 窗口超时 + 1s 重试间隔 ≈ 5s（留 1s 余量）
        Assert.True(sw.ElapsedMilliseconds >= 4000, $"两轮超时+间隔应约 5s，实际 {sw.ElapsedMilliseconds}ms");
        Assert.Contains(statuses, s => s.Contains("超时未收到回复"));
    }

    // ---- 审核修复：入口参数校验（非法输入立即抛参，不得走重试） ----

    [Fact]
    public async Task MoveToAreaAsync_NullRequest_ThrowsArgumentNull()
    {
        var worker = new MachineWorker(() => new MockSerialChannel());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => worker.MoveToAreaAsync(null!, CancellationToken.None));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task MoveToAreaAsync_InvalidSettleSeconds_ThrowsArgumentOutOfRange(double settleSeconds)
    {
        var worker = new MachineWorker(() => new MockSerialChannel());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => worker.MoveToAreaAsync(new MoveRequest(MachineSerial, true, settleSeconds), CancellationToken.None));
    }

    // ---- 审计 MC-01：宿主状态回调异常隔离（回调抛异常不得被当作机台错误） ----

    [Fact]
    public async Task MachineWorker_StatusCallbackThrowsOnSuccessMessage_DoesNotRetry()
    {
        var port = new MockSerialChannel();
        port.EnqueueResponse("ok\r\n");
        port.EnqueueResponse("ok");
        // 宿主回调（UI/日志）在"机台控制指令执行成功"时抛异常——最危险的时序：
        // 指令已全部 ACK，若被 catch 当机台错误将整轮重试、重发移动序列（物理动作重放）
        var worker = new MachineWorker(() => port,
            s => { if (s.Contains("机台控制指令执行成功")) throw new InvalidOperationException("UI boom"); });

        var ok = await worker.MoveToAreaAsync(NewRequest(), CancellationToken.None);

        Assert.True(ok);                        // 回调异常不影响成功判定
        Assert.Equal(2, port.Writes.Count);     // 无重试重发（仅首轮 AT+IO=00/01 各一次）
        Assert.False(port.IsOpen);              // 串口正常关闭
    }

    [Fact]
    public async Task MachineWorker_StatusCallbackThrowsOnOpenMessage_StillSucceeds()
    {
        var port = new MockSerialChannel();
        port.EnqueueResponse("ok\r\n");
        port.EnqueueResponse("ok");
        // 回调在第一条状态消息（尝试打开串口）即抛异常：不得中断打开/发送流程
        var worker = new MachineWorker(() => port, _ => throw new InvalidOperationException("UI boom"));

        var ok = await worker.MoveToAreaAsync(NewRequest(), CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(2, port.Writes.Count);
        Assert.False(port.IsOpen);
    }

    [Fact]
    public async Task MachineWorker_StatusCallbackThrowsInErrorPath_ReturnsFalseWithoutEscaping()
    {
        var port = new MockSerialChannel { OpenError = "拒绝访问" };
        // 回调在 catch 内（"机台控制错误"消息）也抛异常：不得从 catch 逃逸，
        // 错误处理路径本身必须保持有效（两轮 Open 失败后正常返回 false）
        var worker = new MachineWorker(() => port,
            s => { if (s.Contains("机台控制错误")) throw new InvalidOperationException("UI boom"); });

        var ok = await worker.MoveToAreaAsync(NewRequest(), CancellationToken.None);

        Assert.False(ok);                       // 不抛异常逃逸，按机台失败正常返回
    }

    [Fact]
    public async Task MachineWorker_StatusCallbackThrowsInFinally_StillReturnsResult()
    {
        var port = new MockSerialChannel();
        port.EnqueueResponse("ok\r\n");
        port.EnqueueResponse("ok");
        // 回调在 finally（"已关闭机台控制串口"）抛异常：不得覆盖 return true 的返回值
        var worker = new MachineWorker(() => port,
            s => { if (s.Contains("已关闭机台控制串口")) throw new InvalidOperationException("UI boom"); });

        var ok = await worker.MoveToAreaAsync(NewRequest(), CancellationToken.None);

        Assert.True(ok);                        // finally 内回调异常被隔离，返回值不受影响
        Assert.Equal(2, port.Writes.Count);
        Assert.False(port.IsOpen);
    }

    // ---- M67（规格 MachineWorker/docs/specs/m67-machine-serial-close-isolation.md）：
    //      finally 内 Close/Dispose 异常属收尾噪音——不得逃逸覆盖返回值、
    //      不得触发整轮重试的 AT+IO 物理动作重放（对照 BurnWorker 收尾噪音口径） ----

    [Fact]
    public async Task MachineWorker_CloseThrowsOnSuccess_StillReturnsTrueWithoutRetry()
    {
        // AC-1：移动全部 ACK 成功 + Close 抛异常（驱动挂死形态）→ 仍返回 true、无重试重发
        var port = new MockSerialChannel { CloseError = "驱动挂死" };
        port.EnqueueResponse("ok\r\n");
        port.EnqueueResponse("ok");
        var statuses = new List<string>();
        var worker = new MachineWorker(() => port, statuses.Add);

        var ok = await worker.MoveToAreaAsync(NewRequest(), CancellationToken.None);

        Assert.True(ok);                        // 修复前：IOException 从 finally 逃逸覆盖 return true
        Assert.Equal(2, port.Writes.Count);     // 无重试 → 无 AT+IO 物理动作重放
        Assert.Contains(statuses, s => s.Contains("关闭机台控制串口异常"));
    }

    [Fact]
    public async Task MachineWorker_DisposeThrowsOnAckFailure_StillRetriesNormally()
    {
        // AC-2：第一轮 ACK 失败 + Dispose 抛异常 → 收尾噪音隔离，第二轮正常重试成功
        var port = new MockSerialChannel { DisposeError = "释放挂死" };
        port.EnqueueResponse("ERROR\r\n");   // 第一轮：第一条 ACK 失败 → 整轮重试
        port.EnqueueResponse("ok\r\n");      // 第二轮：全部成功
        port.EnqueueResponse("ok");
        var worker = new MachineWorker(() => port);

        var ok = await worker.MoveToAreaAsync(NewRequest(), CancellationToken.None);

        Assert.True(ok);
        // 与既有 FirstRoundAckFails 用例同构：第一轮 1 条 + 第二轮 2 条 = 3 条（重试语义不受收尾噪音影响）
        Assert.Equal(3, port.Writes.Count);
    }

    // ---- 审计 MC-03：串口残留数据不得干扰 ACK 判定（ResetInputBuffer 对齐 DiscardInBuffer） ----

    [Fact]
    public async Task MachineWorker_ResidualGarbage_DoesNotAffectRealAck()
    {
        var port = new MockSerialChannel();
        port.PreloadResidual("garbage\r\n");   // 会话开始前缓冲区已有乱码残留
        port.EnqueueResponse("ok\r\n");
        port.EnqueueResponse("ok");
        var worker = new MachineWorker(() => port);

        var ok = await worker.MoveToAreaAsync(NewRequest(), CancellationToken.None);

        Assert.True(ok);                        // 残留被每条指令前的 ResetInputBuffer 清除
        Assert.Equal(2, port.Writes.Count);     // 无残留干扰 → 首轮即成功，无重试（残留若存活则首轮失败触发重试共 3 条）
    }

    // ---- 审计 MC-06：分片迟到的 ACK 回复不得整体判失败（滑动窗口） ----

    [Fact]
    public async Task MachineWorker_SplitAckLateSecondChunk_StillSucceeds()
    {
        var port = new MockSerialChannel();
        // 机台回复分片迟到：首段 1900ms、末段 2100ms 到达（分片间隔 200ms < 帧尾 300ms，
        // 但末段超过原 2s 绝对窗口——原实现会整体判失败）
        port.EnqueueDelayedResponse("o", TimeSpan.FromMilliseconds(1900));
        port.EnqueueDelayedResponse("k\r\n", TimeSpan.FromMilliseconds(2100));
        // 第二条指令（Write 于第一条完成后 ~2.1s 开始）的回复：2.6s 到达（带换行，到达即判定）
        port.EnqueueDelayedResponse("ok\r\n", TimeSpan.FromMilliseconds(2600));
        var worker = new MachineWorker(() => port);

        var ok = await worker.MoveToAreaAsync(NewRequest(), CancellationToken.None);

        Assert.True(ok);                        // 分片迟到但间隔 < 帧尾窗口 → 等齐后成功
        Assert.Equal(2, port.Writes.Count);     // 首轮即成功，无重试
    }

    // ---- 审计 MC-07：MachineSerial 入口校验（非法输入立即抛参，不走重试） ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task MoveToAreaAsync_BlankMachineSerial_ThrowsArgumentException(string? machineSerial)
    {
        var worker = new MachineWorker(() => new MockSerialChannel());

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => worker.MoveToAreaAsync(new MoveRequest(machineSerial!, true, MoveTimeEnter), CancellationToken.None));
    }

    // ---- 审计 MC-08：无换行回复在帧尾窗口内快速判定（不等满空闲超时） ----

    [Fact]
    public async Task MachineWorker_NoNewlineReply_JudgesWithinFrameTail()
    {
        var port = new MockSerialChannel();
        port.EnqueueResponse("ok\r\n");   // 第一条：带换行立即判定
        port.EnqueueResponse("OK");       // 第二条：无换行 → 帧尾窗口判定（原实现等满空闲 2.5s）
        var worker = new MachineWorker(() => port);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ok = await worker.MoveToAreaAsync(NewRequest(), CancellationToken.None);
        sw.Stop();

        Assert.True(ok);
        // 帧尾 300ms 判定 vs 原空闲 2.5s：1.5s 阈值区分
        Assert.True(sw.ElapsedMilliseconds < 1500, $"无换行回复应在帧尾窗口内判定，实际 {sw.ElapsedMilliseconds}ms");
    }
}
