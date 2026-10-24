using ML.Core.Maven;
using ML.Core.Rules;
using ML.Core.Versioning;

namespace ML.Core.Install;

/// <summary>
/// 判断一个库的哪个 natives 分类器适用于当前机器。
///
/// <para><b>这里有个真坑，必须按架构过滤。</b>
/// 1.19 起的 version.json 里，三个 Windows natives jar ——
/// <c>natives-windows</c>、<c>natives-windows-x86</c>、<c>natives-windows-arm64</c> ——
/// 的 rules 全部只写 <c>{"os":{"name":"windows"}}</c>，不做架构判断。
/// 也就是说在 64 位 Windows 上，<b>三条规则全部放行</b>。
/// 如果照着规则把它们统统解压进同一个目录，后写的会覆盖先写的，
/// 游戏启动时就会拿到错位数的 dll，报 <c>UnsatisfiedLinkError</c> 或者直接崩。</para>
/// </summary>
public static class NativeClassifier
{
    public const string Prefix = "natives-";

    /// <summary>
    /// 分类器里表示架构的后缀。<c>x86_64</c> 在所有平台上都是「没有后缀」。
    /// </summary>
    public static string ArchitectureSuffix(EnvironmentProfile env)
    {
        ArgumentNullException.ThrowIfNull(env);

        return env.Arch switch
        {
            "x86" => "x86",
            "arm64" => "arm64",
            "arm32" => "arm32",
            _ => string.Empty
        };
    }

    /// <summary>
    /// 旧式 <c>natives</c> 映射：<c>{ "windows": "natives-windows-${arch}" }</c>。
    /// <c>${arch}</c> 在旧格式里是 <c>32</c> / <c>64</c>，不是 <c>x86</c> / <c>x86_64</c>。
    /// </summary>
    public static string? ResolveClassifierKey(LibraryEntry library, EnvironmentProfile env)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(env);

        if (library.Natives is null
            || !library.Natives.TryGetValue(env.OsName, out var template)
            || string.IsNullOrEmpty(template))
        {
            return null;
        }

        return template.Replace("${arch}", LegacyArchToken(env.Arch), StringComparison.Ordinal);
    }

    /// <summary>1.19+ 的写法：classifier 直接写在 Maven 坐标里。</summary>
    public static bool IsModernNatives(string? mavenName)
    {
        return MavenCoordinate.TryParse(mavenName, out var coordinate)
               && coordinate.Classifier is not null
               && coordinate.Classifier.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 分类器对应的架构是否与当前机器一致。
    /// 同时认两套词汇：现代的 <c>x86</c>/<c>arm64</c>，旧式的 <c>32</c>/<c>64</c>。
    /// </summary>
    public static bool MatchesArchitecture(string classifier, EnvironmentProfile env)
    {
        ArgumentNullException.ThrowIfNull(env);

        if (string.IsNullOrEmpty(classifier))
        {
            return false;
        }

        var parts = classifier.Split('-');

        // parts[0] 是 "natives"，parts[1] 是平台名，剩下的才是架构部分。
        var architecture = parts.Length > 2
            ? string.Join('-', parts[2..]).ToLowerInvariant()
            : string.Empty;

        architecture = architecture switch
        {
            "32" => "x86",
            "64" => string.Empty,
            _ => architecture
        };

        return string.Equals(architecture, ArchitectureSuffix(env), StringComparison.OrdinalIgnoreCase);
    }

    private static string LegacyArchToken(string arch) => arch switch
    {
        "x86" => "32",
        "arm32" => "32",
        _ => "64"
    };
}
