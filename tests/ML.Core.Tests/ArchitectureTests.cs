using ML.Core.Install;
using ML.Features.Download;
using ML.Features.Install;

namespace ML.Core.Tests;

/// <summary>
/// 架构约束的机器检查。这些不是「代码风格」，是产品定位的边界：
/// 一旦有人越过，插件化就会退化成单体启动器，而这种退化是渐进的、评审看不出来的。
/// </summary>
public sealed class ArchitectureTests
{
    /// <summary>
    /// 内核不许联网。
    /// </summary>
    /// <remarks>
    /// 判据不是「文件里有没有 HttpClient」，而是「程序集引用清单里有没有网络程序集」——
    /// 前者可以绕（反射、第三方库），后者绕不过去，编译期就决定了。
    /// <para>
    /// 这条约束的来历：最初内核里带着完整的下载引擎（断点续传、SHA1 校验、并发队列）。
    /// 它当然能工作，但它让「下载源镜像」「整合包导入」「模组更新」这些功能有了搬进内核的
    /// 理由，而那个方向就是 PCL/HMCL 的形状。产品定位是「启动器内核 + 插件」，
    /// 所以下载必须待在内核外面。
    /// </para>
    /// </remarks>
    [Fact]
    public void 内核程序集不引用任何网络程序集()
    {
        var referenced = typeof(GamePaths).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .ToArray();

        string[] forbidden = [.. referenced.Where(name =>
            name.StartsWith("System.Net", StringComparison.Ordinal))];

        Assert.Empty(forbidden);
    }

    /// <summary>内核不引用功能层——依赖方向只能功能层 → 内核，反过来就是循环。</summary>
    [Fact]
    public void 内核不引用功能层()
    {
        var referenced = typeof(GamePaths).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty);

        Assert.DoesNotContain("ML.Features", referenced);
    }

    /// <summary>下载与装机确实不在内核里。搬走要真搬，不能留个转发壳。</summary>
    [Fact]
    public void 下载与装机类型住在功能层()
    {
        Assert.Equal("ML.Features", typeof(FileDownloader).Assembly.GetName().Name);
        Assert.Equal("ML.Features", typeof(GameInstaller).Assembly.GetName().Name);
        Assert.Equal("ML.Core", typeof(GamePaths).Assembly.GetName().Name);
    }
}
