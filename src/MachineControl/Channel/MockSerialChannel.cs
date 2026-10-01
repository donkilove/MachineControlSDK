using System.IO;

namespace MachineControl.Channel;

/// <summary>
/// 可编程模拟串口通道：按队列回放响应、记录写入、可注入打开失败，
/// 便于离线开发与自动化测试（对照 ScpiInstrument 的 MockInstrumentChannel）。
/// </summary>
public sealed class MockSerialChannel : ISerialChannel
{
    private readonly Queue<string> _responses = new();
    private readonly List<string> _writes = new();
    private readonly List<(string Text, DateTimeOffset DueAt)> _delayed = new();
    private string _residual = "";   // 模拟"已到达但未读"的接收缓冲残留（会话开始前缓冲区已有数据）

    /// <summary>已写入的全部文本（按写入顺序）</summary>
    public IReadOnlyList<string> Writes => _writes;

    /// <summary>设置后 Open 将抛出 IOException（模拟占用/拒绝访问）</summary>
    public string? OpenError { get; set; }

    /// <summary>设置后 Close 将抛出 IOException（模拟关闭时驱动故障——M67 收尾噪音注入）</summary>
    public string? CloseError { get; set; }

    /// <summary>设置后 Dispose 将抛出 IOException（模拟释放时驱动故障——M67 收尾噪音注入）</summary>
    public string? DisposeError { get; set; }

    /// <summary>最近一次成功 Open 的串口名（未成功打开过为 null）</summary>
    public string? LastOpenedPort { get; private set; }

    /// <summary>最近一次成功 Open 的波特率（未成功打开过为 null）</summary>
    public int? LastBaudRate { get; private set; }

    public bool IsOpen { get; private set; }

    /// <summary>入队一条响应；ReadAvailable 按 FIFO 回放，队列空时返回空串</summary>
    public void EnqueueResponse(string response) => _responses.Enqueue(response);

    /// <summary>入队一条延迟响应：指定延迟后才可读（模拟机台回复分片/迟到的时序）</summary>
    public void EnqueueDelayedResponse(string response, TimeSpan delay)
        => _delayed.Add((response, DateTimeOffset.UtcNow + delay));

    /// <summary>预置"已到达但未读"的接收残留（模拟会话开始前缓冲区已有数据，如乱码/上次残留）。
    /// 与 EnqueueResponse 的区别：残留会被 ResetInputBuffer 丢弃，未来应答不受影响。</summary>
    public void PreloadResidual(string data) => _residual += data;

    public void Open(string portName, int baudRate)
    {
        if (OpenError is not null)
        {
            throw new IOException(OpenError);
        }

        LastOpenedPort = portName;
        LastBaudRate = baudRate;
        IsOpen = true;
    }

    public void Write(string text)
    {
        // 审计复审：与真实 SerialPortChannel.Write 一致——未打开时抛异常
        if (!IsOpen)
        {
            throw new InvalidOperationException("串口未打开");
        }

        _writes.Add(text);
    }

    public string ReadAvailable()
    {
        // 审计复审：与真实 SerialPortChannel.ReadAvailable 一致——未打开时返回空串
        if (!IsOpen)
        {
            return "";
        }

        // 真实语义：残留与新应答同在接收缓冲区，先到先读（残留优先、一次读完）
        if (_residual.Length > 0)
        {
            var head = _residual;
            _residual = "";
            return head;
        }

        // 延迟响应：到期项按到期顺序拼接返回（同一接收缓冲区语义）
        var now = DateTimeOffset.UtcNow;
        var due = _delayed.Where(d => d.DueAt <= now).Select(d => d.Text).ToList();
        if (due.Count > 0)
        {
            _delayed.RemoveAll(d => d.DueAt <= now);
            return string.Join("", due);
        }

        return _responses.Count > 0 ? _responses.Dequeue() : "";
    }

    public void ResetInputBuffer()
    {
        // 审计 MC-03：对齐真实 SerialPortChannel.DiscardInBuffer——丢弃"已到达未读"的
        // 接收残留，不影响未来应答（EnqueueResponse 预置的响应队列）
        _residual = "";
        // 审计复审：已到期但未读的延迟响应同样属"已到达未读"，随重置丢弃；
        // 未到期的保留（尚未到达，不受影响）
        var now = DateTimeOffset.UtcNow;
        _delayed.RemoveAll(d => d.DueAt <= now);
    }

    public void Close()
    {
        if (CloseError is not null)
        {
            throw new IOException(CloseError);
        }

        IsOpen = false;
    }

    public void Dispose()
    {
        if (DisposeError is not null)
        {
            throw new IOException(DisposeError);
        }
    }
}
