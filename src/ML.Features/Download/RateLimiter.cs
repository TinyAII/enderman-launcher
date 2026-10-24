using System.Diagnostics;

namespace ML.Features.Download;

/// <summary>
/// 全局令牌桶限速。
/// </summary>
/// <remarks>
/// <para>
/// 必须全局共用一份：如果每个连接各自限速，64 个连接就是 64 倍速度，限速等于没有。
/// </para>
/// <para>
/// 速率可以在运行中改（用户在设置里拖动），<c>&lt;= 0</c> 表示不限速。
/// 这里刻意用"先确认够、再扣、不够就等"的简单模型，不追求毫秒级平滑——
/// 下载速度的观感只需要稳定在某个量级。
/// </para>
/// </remarks>
public sealed class RateLimiter
{
    private readonly object _gate = new();

    private double _tokens;
    private long _lastTimestamp;
    private long _bytesPerSecond;

    public RateLimiter(long bytesPerSecond = 0)
    {
        _bytesPerSecond = Math.Max(0, bytesPerSecond);
        _lastTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>当前限速（字节/秒）。0 表示不限。</summary>
    public long BytesPerSecond
    {
        get
        {
            lock (_gate)
            {
                return _bytesPerSecond;
            }
        }
    }

    public void Configure(long bytesPerSecond)
    {
        lock (_gate)
        {
            _bytesPerSecond = Math.Max(0, bytesPerSecond);

            // 收紧速率时把余额清掉：否则刚切到 1 MB/s 还能先飙完旧额度
            _tokens = 0;
            _lastTimestamp = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>申请 <paramref name="bytes"/> 的额度；不够就等到够为止。</summary>
    public async Task WaitAsync(int bytes, CancellationToken cancellationToken)
    {
        if (bytes <= 0)
        {
            return;
        }

        while (true)
        {
            TimeSpan delay;

            lock (_gate)
            {
                if (_bytesPerSecond <= 0)
                {
                    return;
                }

                Refill();

                // 够一次就扣掉走人。**扣减只能在确认能走时做一次**：
                // 放在等待之前会让每轮循环都扣一遍，等待被摊薄成几分之一（实测 2.7s 变 0.85s）。
                if (_tokens >= bytes)
                {
                    _tokens -= bytes;
                    return;
                }

                delay = TimeSpan.FromSeconds((bytes - _tokens) / _bytesPerSecond);
            }

            await Task.Delay(delay < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : delay, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private void Refill()
    {
        long now = Stopwatch.GetTimestamp();
        double seconds = (now - _lastTimestamp) / (double)Stopwatch.Frequency;
        _lastTimestamp = now;

        if (_bytesPerSecond <= 0)
        {
            _tokens = 0;
            return;
        }

        _tokens += seconds * _bytesPerSecond;

        // 最多攒 1 秒的额度，避免长时间空闲后一次性放出巨量突发
        if (_tokens > _bytesPerSecond)
        {
            _tokens = _bytesPerSecond;
        }
    }
}
