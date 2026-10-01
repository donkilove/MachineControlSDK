using System.Text;
using MachineControl.Channel;

namespace MachineControl;

/// <summary>
/// 机台移动执行器：按区域发送 AT+IO 指令序列并等待 ACK（"ok"），
/// 成功后等待到位时间。整轮最多尝试 2 次（含首次），失败重试间隔 1s。
/// </summary>
public sealed class MachineWorker
{
    private const int MaxAttempts = 2;
    private const int RetryDelayMs = 1000;
    private const int IdleAckWindowMs = 2500;    // 审计 MC-06：空闲超时（距上次收到数据），原 2s 绝对窗口改滑动
    private const int FrameTailMs = 300;         // 审计 MC-08：无换行回复的帧尾等待窗口（收到数据后无新数据即判定帧结束）
    private const int TotalAckBudgetMs = 4000;   // 审计 MC-06：总预算，防分片持续到达导致无限等待
    private const int ReadPollMs = 100;
    private const int MaxAckLength = 256;   // 审核修复：回复长度上限（正常 "ok" 仅 2 字符）

    private readonly Func<ISerialChannel> _channelFactory;
    private readonly Action<string>? _status;
    private readonly int _baudRate;

    /// <param name="channelFactory">每次执行新建串口通道的工厂（执行结束即关闭释放）</param>
    /// <param name="status">可选状态回调（如宿主状态栏）；SDK 独立使用可不传。
    /// 回调异常与机台控制流隔离：抛出的异常被吞掉（仅 Debug 记录），绝不当作机台错误。</param>
    /// <param name="baudRate">机台控制串口波特率（协议为 9600 8N1）</param>
    public MachineWorker(Func<ISerialChannel> channelFactory, Action<string>? status = null, int baudRate = 9600)
    {
        _channelFactory = channelFactory;
        _status = WrapStatusCallback(status);
        _baudRate = baudRate;
    }

    /// <summary>
    /// 宿主状态回调与控制流隔离（审计 MC-01）：回调（UI/日志）异常不得被当作机台错误，
    /// 否则指令已全部 ACK 后回调抛异常会触发整轮重试、重发 AT+IO 移动序列（物理动作重放），
    /// finally 内回调抛异常还会覆盖返回值。仅 Debug 记录便于宿主排查自身缺陷，Release 零开销。
    /// </summary>
    private static Action<string>? WrapStatusCallback(Action<string>? status)
    {
        if (status is null)
        {
            return null;
        }

        return message =>
        {
            try
            {
                status(message);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MachineControl] 状态回调异常已隔离: {ex}");
            }
        };
    }

    public async Task<bool> MoveToAreaAsync(MoveRequest request, CancellationToken ct)
    {
        // 审核修复：入口参数校验——非法输入立即抛参，不得以"重试耗尽后 false"掩盖调用方错误
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MachineSerial, "request.MachineSerial");   // 审计 MC-07：串口名非空白校验（paramName 指向成员）
        if (!double.IsFinite(request.SettleSeconds) || request.SettleSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request), request.SettleSeconds, "SettleSeconds 必须为有限非负数（秒）");
        }

        for (var retry = 0; retry < MaxAttempts; retry++)
        {
            ISerialChannel? ser = null;
            try
            {
                _status?.Invoke($"尝试打开机台控制串口 {request.MachineSerial}，第 {retry + 1}/{MaxAttempts} 次");
                ser = _channelFactory();
                ser.Open(request.MachineSerial, _baudRate);

                var area = request.IsAreaA ? "A" : "B";
                _status?.Invoke($"发送{area}区移动指令");

                var sequence = MachineProtocol.GetSequence(request.IsAreaA);
                var allAcked = true;
                foreach (var cmd in sequence)
                {
                    if (!await SendWithAckAsync(ser, cmd, ct))
                    {
                        allAcked = false;
                        break;
                    }
                }

                if (!allAcked)
                {
                    // 本轮失败：重试（原版 continue → 触发 finally 关闭串口）。
                    // 审核修复：按协议规格 §3.3，ACK 失败同样需要 1s 重试间隔（此前仅异常路径有间隔）
                    if (retry < MaxAttempts - 1)
                    {
                        await Task.Delay(RetryDelayMs, ct);
                    }

                    continue;
                }

                _status?.Invoke("机台控制指令执行成功");
                _status?.Invoke($"等待移动到位时间: {request.SettleSeconds}秒");
                await Task.Delay(TimeSpan.FromSeconds(request.SettleSeconds), ct);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                _status?.Invoke($"机台控制错误: {e.Message}");
                if (retry >= MaxAttempts - 1)
                {
                    return false;
                }

                await Task.Delay(RetryDelayMs, ct);
            }
            finally
            {
                // M67（对照 BurnWorker 收尾噪音口径）：Close/Dispose 异常属收尾噪音——
                // 不得从 finally 逃逸覆盖返回值（移动已成功时会被判失败 → 整轮重试 →
                // AT+IO 物理动作重放），也不得干扰 catch 的重试/返回语义。异常仅记状态回调。
                if (ser is { IsOpen: true })
                {
                    try
                    {
                        ser.Close();
                        _status?.Invoke($"已关闭机台控制串口 {request.MachineSerial}");
                    }
                    catch (Exception closeEx)
                    {
                        _status?.Invoke($"关闭机台控制串口异常（收尾噪音，不影响移动结果）: {closeEx.Message}");
                    }
                }

                try
                {
                    ser?.Dispose();
                }
                catch (Exception disposeEx)
                {
                    _status?.Invoke($"释放机台控制串口异常（收尾噪音）: {disposeEx.Message}");
                }
            }
        }

        return false;
    }

    private async Task<bool> SendWithAckAsync(ISerialChannel ser, string command, CancellationToken ct)
    {
        ser.ResetInputBuffer();
        _status?.Invoke($"发送指令: {command}");
        ser.Write(MachineProtocol.BuildLine(command));

        var sb = new StringBuilder();
        // MC-02：Stopwatch 单调计时——DateTime.UtcNow 受 NTP 回拨/跳变影响会失真 ACK 窗口
        //（回拨则空闲超时延长至多秒级，跳前则提前判失败）；与 BurnWorker 同口径。
        // sinceStart 对齐总预算、sinceData 对齐空闲/帧尾判定（等价于原 start/lastData 差值）
        var sinceStart = System.Diagnostics.Stopwatch.StartNew();
        var sinceData = System.Diagnostics.Stopwatch.StartNew();
        while (sinceStart.ElapsedMilliseconds < TotalAckBudgetMs)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = ser.ReadAvailable();
            if (chunk.Length > 0)
            {
                sb.Append(chunk);
                sinceData.Restart();   // 审计 MC-06：收到数据刷新空闲计时——分片迟到不再整体判失败
                if (sb.Length > MaxAckLength)
                {
                    // 审核修复：回复长度上限，防畸形/恶意长帧刷爆窗口
                    _status?.Invoke("错误：回复长度超限");
                    return false;
                }

                if (chunk.Contains('\n'))
                {
                    break;
                }
            }
            else if (sinceData.ElapsedMilliseconds >= (sb.Length > 0 ? FrameTailMs : IdleAckWindowMs))
            {
                // 审计 MC-08：收到过数据 → 帧尾窗口判定（无换行回复快速判定，不等满空闲超时）；
                // 完全无数据 → 空闲超时（无响应）
                break;
            }

            await Task.Delay(ReadPollMs, ct);
        }

        var response = sb.ToString().Trim();
        if (response.Length > 0)
        {
            _status?.Invoke($"收到回复: {response}");
        }

        if (MachineProtocol.IsAck(response))
        {
            return true;
        }

        _status?.Invoke(response.Length == 0
            ? $"错误：发送指令 {command} 后超时未收到回复"
            : $"错误：收到的回复不是ok，而是：{response}");
        return false;
    }
}
