using ML.Core.Install;
using ML.Core.Maven;
using ML.Core.Rules;
using ML.Core.Versioning;

namespace ML.Core.Launch;

/// <summary>classpath 构造结果。</summary>
public sealed record ClasspathPlan(
    IReadOnlyList<string> Entries,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> MissingFiles)
{
    /// <summary>拼成 <c>-cp</c> 需要的单串（Windows 分隔符是 <c>;</c>）。</summary>
    public string AsArgument => string.Join(Path.PathSeparator, Entries);
}

/// <summary>
/// 从合并后的 <c>libraries</c> 拼出 classpath。
/// </summary>
public static class ClasspathBuilder
{
    /// <summary>
    /// 拼 classpath：按 version.json 里的顺序，去掉 natives 分类器条目，末尾接 client.jar。
    /// </summary>
    /// <param name="paths">目录布局。</param>
    /// <param name="libraries">合并后的库列表（父在前、子在后）。</param>
    /// <param name="env">用于求值库自带 rules 的环境。</param>
    /// <param name="versionId">用来定位 client.jar。</param>
    public static ClasspathPlan Build(
        GamePaths paths,
        IEnumerable<LibraryEntry>? libraries,
        EnvironmentProfile env,
        string versionId)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(env);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);

        var entries = new List<string>();
        var warnings = new List<string>();
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var library in libraries ?? [])
        {
            if (!RuleEvaluator.IsAllowed(library.Rules, env))
            {
                continue;
            }

            // natives 分类器的 jar 只用来解压，不进 classpath：原生库由
            // -Djava.library.path 指向的解压目录提供，把纯 dll 的 jar 塞进
            // classpath 只会让类加载器去扫一堆非 class 条目。
            // 这条结论由真机启动验证——如果启动报 UnsatisfiedLinkError，说明判断反了。
            if (NativeClassifier.IsModernNatives(library.Name))
            {
                continue;
            }

            string? relativePath = null;

            if (!string.IsNullOrEmpty(library.Downloads?.Artifact?.Path))
            {
                relativePath = library.Downloads.Artifact.Path;
            }
            else if (MavenCoordinate.TryParse(library.Name, out var coordinate))
            {
                // Fabric / Quilt 只给仓库前缀，不给 downloads，路径得自己按坐标算。
                relativePath = coordinate.RelativePath;
            }
            else
            {
                warnings.Add($"库坐标无法解析，已从 classpath 跳过：{library.Name}");
                continue;
            }

            if (!seen.Add(relativePath))
            {
                continue;
            }

            entries.Add(paths.LibraryFile(relativePath));
        }

        // client.jar 放最后：它只含 net.minecraft.*，位置放哪都能被找到，
        // 但放最后可以让 mod 加载器注入的库优先——Forge 的 BootstrapLauncher 依赖这个顺序。
        string clientJar = paths.ClientJar(versionId);
        if (seen.Add(clientJar))
        {
            entries.Add(clientJar);
        }

        // 落盘检查：classpath 里指向不存在文件的条目会让 java 在启动瞬间抛出
        // NoClassDefFoundError / ClassNotFoundException，错误信息只提某个类名，
        // 看不出是"文件根本没下到这儿"。把检查放在这里，调用方能在起 JVM 之前拦住。
        foreach (var entry in entries)
        {
            if (!File.Exists(entry))
            {
                missing.Add(entry);
            }
        }

        return new ClasspathPlan(entries, warnings, missing);
    }
}
