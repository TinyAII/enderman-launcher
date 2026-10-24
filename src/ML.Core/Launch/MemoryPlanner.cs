namespace ML.Core.Launch;

/// <summary>
/// 「自动配置」内存怎么算。
/// </summary>
/// <remarks>
/// <para>
/// 刻意做成纯函数：给定总内存、可用内存、模组数量就能算，不碰系统 API，便于单测和解释。
/// 规则要能对用户讲清楚，否则"自动"就是个黑箱，出问题没人敢调：
/// </para>
/// <list type="number">
/// <item>基准 = 物理内存的 15%，夹在 2 GB ~ 4 GB —— 2 GB 是原版底线，4 GB 是原版上限，再多是浪费。</item>
/// <item>每 50 个模组加 512 MB，最多加 2 GB —— 模组越多，堆越吃紧。</item>
/// <item>不超过物理内存的一半，且不超过 8 GB —— 留一半给系统。</item>
/// <item>结果对齐到 256 MB，便于在界面上读数。</item>
/// <para>
/// <b>刻意不参考"当前可用内存"。</b>早期版本按可用值压上限，结果 16 GB 机器上只给出 1.8 GB：
/// 因为 Windows 平时就把内存吃满当缓存用，"可用"永远很小。游戏启动时系统会回收这些缓存，
/// 按瞬时可用值算只会把堆压到不够用。所以这里只看总量。
/// </para>
/// </list>
/// </remarks>
public static class MemoryPlanner
{
    public const int BaselineMb = 2048;
    public const int BaseRatioPercent = 15;
    public const int MinBaseMb = 2048;
    public const int MaxBaseMb = 4096;
    public const int StepMb = 512;
    public const int ModsPerStep = 50;
    public const int MaxModBonusMb = 2048;
    public const int HardCapMb = 8192;
    public const int FloorMb = 512;
    public const int AlignmentMb = 256;

    /// <summary>按总内存（MB）、可用内存（MB）、模组数量推荐一个堆大小。</summary>
    /// <remarks><paramref name="availableMb"/> 只用于日志/界面，不参与计算，理由见类型注释。</remarks>
    public static int RecommendMb(long totalMb, long availableMb, int modCount)
    {
        _ = availableMb;

        int baseMb = (int)Math.Clamp(
            totalMb * BaseRatioPercent / 100,
            MinBaseMb,
            MaxBaseMb);

        int modBonus = Math.Min(MaxModBonusMb, Math.Max(0, modCount) / ModsPerStep * StepMb);
        int wanted = baseMb + modBonus;

        // 上限：物理内存的一半、但不超过 8 GB。
        long cap = Math.Min(HardCapMb, totalMb / 2);

        // 机器很小的时候（半内存都不够基线），退到四分之一，别直接给 512 起步。
        if (cap < FloorMb)
        {
            cap = Math.Max(FloorMb, totalMb / 4);
        }

        int result = (int)Math.Clamp(wanted, FloorMb, cap);
        return Align(result);
    }

    private static int Align(int mb) => (int)(Math.Round(mb / (double)AlignmentMb) * AlignmentMb);
}
