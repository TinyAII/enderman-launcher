using ML.Core.Configuration;

namespace ML.Core.Tests;

/// <summary>
/// 下载参数的默认值。
/// </summary>
/// <remarks>
/// 这里踩过一次：`System.Text.Json` 的源生成器反序列化**缺字段**时给的是 0，
/// 不是属性初始化器里的值。所以"默认 64 线程"必须在 <see cref="LauncherSettings.Normalize"/> 里兜底，
/// 否则旧配置读进来就是 0，界面上滑块卡在最左边。
/// </remarks>
public sealed class DownloadSettingsDefaultTests
{
    [Fact]
    public void 缺字段时线程数回落到64()
    {
        var settings = new LauncherSettings { DownloadThreads = 0 }.Normalize();

        Assert.Equal(64, settings.DownloadThreads);
    }

    [Fact]
    public void 缺字段时并发任务回落到1()
    {
        var settings = new LauncherSettings { DownloadParallelTasks = 0 }.Normalize();

        Assert.Equal(1, settings.DownloadParallelTasks);
    }

    [Fact]
    public void 缺字段时限速是0也就是不限()
    {
        var settings = new LauncherSettings { DownloadSpeedLimitMb = 0 }.Normalize();

        Assert.Equal(0, settings.DownloadSpeedLimitMb);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(64, 64)]
    [InlineData(256, 256)]
    [InlineData(999, 256)]
    [InlineData(-5, 64)]
    public void 线程数被夹到合法区间(int input, int expected)
    {
        var settings = new LauncherSettings { DownloadThreads = input }.Normalize();

        Assert.Equal(expected, settings.DownloadThreads);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(10, 10)]
    [InlineData(50, 10)]
    public void 并发任务被夹到1到10(int input, int expected)
    {
        var settings = new LauncherSettings { DownloadParallelTasks = input }.Normalize();

        Assert.Equal(expected, settings.DownloadParallelTasks);
    }
}
