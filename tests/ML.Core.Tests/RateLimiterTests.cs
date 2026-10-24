using System.Diagnostics;
using ML.Features.Download;

namespace ML.Core.Tests;

/// <summary>
/// 令牌桶限速。这是"限速看起来没用"最容易出问题的地方：
/// 每轮循环都扣令牌会把等待摊薄，实测过 2.7 秒变成 0.85 秒。
/// </summary>
public sealed class RateLimiterTests
{
    [Fact]
    public async Task 不限速时不等待()
    {
        var limiter = new RateLimiter(0);
        var watch = Stopwatch.StartNew();

        for (int i = 0; i < 100; i++)
        {
            await limiter.WaitAsync(64 * 1024, CancellationToken.None);
        }

        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds < 500, $"不限速却等了 {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task 限速后耗时不小于理论下限()
    {
        const int rate = 200 * 1024;      // 200 KB/s
        const int chunk = 64 * 1024;      // 每块 64 KB
        const int count = 6;              // 共 384 KB → 理论 1.92 秒

        var limiter = new RateLimiter(rate);
        var watch = Stopwatch.StartNew();

        for (int i = 0; i < count; i++)
        {
            await limiter.WaitAsync(chunk, CancellationToken.None);
        }

        watch.Stop();

        double expected = (double)chunk * count / rate;
        Assert.True(watch.Elapsed.TotalSeconds >= expected * 0.9,
            $"{count * chunk / 1024} KB 在 {rate / 1024} KB/s 下应至少 {expected:F2}s，实际 {watch.Elapsed.TotalSeconds:F2}s");
    }
}
