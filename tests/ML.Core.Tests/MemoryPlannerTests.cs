using ML.Core.Launch;

namespace ML.Core.Tests;

/// <summary>
/// 自动内存规则。这些数字会被写进 --xmx，给少了游戏崩、给多了系统卡，
/// 所以每条边界都钉住，改算法时先看这里。
/// </summary>
public sealed class MemoryPlannerTests
{
    [Fact]
    public void 十六G机器原版给2_5G()
    {
        Assert.Equal(2560, MemoryPlanner.RecommendMb(16 * 1024, 2 * 1024, modCount: 0));
    }

    [Fact]
    public void 八G机器不会低于基线两G()
    {
        Assert.Equal(2048, MemoryPlanner.RecommendMb(8 * 1024, 1024, modCount: 0));
    }

    [Fact]
    public void 三十二G机器封顶四G基准()
    {
        Assert.Equal(4096, MemoryPlanner.RecommendMb(32 * 1024, 8 * 1024, modCount: 0));
    }

    [Fact]
    public void 每50个模组加512M()
    {
        // 16G 基准 2457(对齐前) + 120 个模组的 1024 = 3481 → 对齐 3584
        Assert.Equal(3584, MemoryPlanner.RecommendMb(16 * 1024, 8 * 1024, modCount: 120));
    }

    [Fact]
    public void 模组加成有上限()
    {
        // 1000 个模组也只加 2048，不会无限涨
        Assert.Equal(4608, MemoryPlanner.RecommendMb(16 * 1024, 8 * 1024, modCount: 1000));
    }

    [Fact]
    public void 结果永远不超过物理内存一半()
    {
        int mb = MemoryPlanner.RecommendMb(4 * 1024, 512, modCount: 500);

        Assert.True(mb <= 2 * 1024, $"给了 {mb} MB，超过 4G 机器的一半");
    }

    [Fact]
    public void 可用内存不影响结果()
    {
        // 刻意不看 available：Windows 平时把内存当缓存吃满，按瞬时可用值算会把堆压到不够
        Assert.Equal(
            MemoryPlanner.RecommendMb(16 * 1024, 14 * 1024, 10),
            MemoryPlanner.RecommendMb(16 * 1024, 1024, 10));
    }

    [Fact]
    public void 总内存读不到时不返回零()
    {
        Assert.True(MemoryPlanner.RecommendMb(0, 0, 0) > 0);
    }
}
