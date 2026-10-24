using ML.Core.Maven;

namespace ML.Core.Versioning;

/// <summary>
/// 把继承链合并成一份可用的 version.json。
/// 纯函数：不读文件、不联网，调用方负责按 <c>inheritsFrom</c> 把链拼好。
/// </summary>
public static class VersionMerger
{
    /// <summary>
    /// 合并继承链。
    /// </summary>
    /// <param name="chain">
    /// 顺序必须是从最祖先到最终子版本：<c>chain[0]</c> 是根（如 1.20.1），
    /// <c>chain[^1]</c> 是最终要启动的版本（如 1.20.1-forge-47.2.0）。
    /// </param>
    public static VersionMetadata Merge(IReadOnlyList<VersionMetadata> chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        if (chain.Count == 0)
        {
            throw new ArgumentException("继承链不能为空", nameof(chain));
        }

        if (chain.Count == 1)
        {
            return Clone(chain[0]);
        }

        return new VersionMetadata
        {
            // ── 标量：更“子”的版本覆盖祖先 ──────────────────────────
            Id = LastRef(chain, v => v.Id),
            MainClass = LastRef(chain, v => v.MainClass),
            MinecraftArguments = LastRef(chain, v => v.MinecraftArguments),
            Assets = LastRef(chain, v => v.Assets),
            Type = LastRef(chain, v => v.Type),
            AssetIndex = LastRef(chain, v => v.AssetIndex),
            JavaVersion = LastRef(chain, v => v.JavaVersion),
            Logging = LastRef(chain, v => v.Logging),
            Downloads = LastRef(chain, v => v.Downloads),
            ReleaseTime = LastRef(chain, v => v.ReleaseTime),
            Time = LastRef(chain, v => v.Time),
            MinimumLauncherVersion = LastValue(chain, v => v.MinimumLauncherVersion),
            ComplianceLevel = LastValue(chain, v => v.ComplianceLevel),

            // 合并后 inheritsFrom 失去意义，必须清掉，否则会诱导下游再解析一次
            InheritsFrom = null,

            Libraries = MergeLibraries(chain),
            Arguments = MergeArguments(chain)
        };
    }

    /// <summary>
    /// libraries 合并：子版本的库排在前面，按 Maven 坐标去重，先出现的胜出。
    /// 子在前是为了让 Forge/NeoForge 覆盖同名 vanilla 库时真的生效。
    /// </summary>
    private static List<LibraryEntry>? MergeLibraries(IReadOnlyList<VersionMetadata> chain)
    {
        bool any = false;
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            if (chain[i].Libraries is { Count: > 0 })
            {
                any = true;
                break;
            }
        }

        if (!any)
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<LibraryEntry>();

        // 从子到祖先遍历，保证子的条目先入列
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            if (chain[i].Libraries is not { Count: > 0 } libs)
            {
                continue;
            }

            foreach (var lib in libs)
            {
                if (seen.Add(LibraryKey(lib)))
                {
                    merged.Add(lib);
                }
            }
        }

        return merged;
    }

    /// <summary>
    /// 坐标解析不出来时退化成整行字符串做键——宁可多留一条重复库，
    /// 也不要因为一条脏坐标把两条不同的库误判成同一条。
    /// </summary>
    private static string LibraryKey(LibraryEntry lib)
    {
        if (MavenCoordinate.TryParse(lib.Name, out var coord))
        {
            return coord.Key;
        }

        return "raw:" + (lib.Name ?? string.Empty);
    }

    /// <summary>
    /// arguments 合并：祖先在前、子在后依次拼接。
    /// game 与 jvm 各自独立拼接——子版本加的 -tweakClass 之类必须排在 vanilla 参数之后。
    /// </summary>
    private static ArgumentsBlock? MergeArguments(IReadOnlyList<VersionMetadata> chain)
    {
        List<ArgumentEntry>? game = Concat(chain, b => b.Game);
        List<ArgumentEntry>? jvm = Concat(chain, b => b.Jvm);

        if (game is null && jvm is null)
        {
            return null;
        }

        return new ArgumentsBlock { Game = game, Jvm = jvm };
    }

    private static List<ArgumentEntry>? Concat(
        IReadOnlyList<VersionMetadata> chain,
        Func<ArgumentsBlock, List<ArgumentEntry>?> selector)
    {
        List<ArgumentEntry>? result = null;

        // 祖先 → 子
        foreach (var version in chain)
        {
            if (version.Arguments is null)
            {
                continue;
            }

            if (selector(version.Arguments) is not { Count: > 0 } part)
            {
                continue;
            }

            result ??= [];
            result.AddRange(part);
        }

        return result;
    }

    private static T? LastRef<T>(IReadOnlyList<VersionMetadata> chain, Func<VersionMetadata, T?> selector)
        where T : class
    {
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            if (selector(chain[i]) is { } value)
            {
                return value;
            }
        }

        return null;
    }

    private static T? LastValue<T>(IReadOnlyList<VersionMetadata> chain, Func<VersionMetadata, T?> selector)
        where T : struct
    {
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            if (selector(chain[i]) is { } value)
            {
                return value;
            }
        }

        return null;
    }

    private static VersionMetadata Clone(VersionMetadata source) => new()
    {
        Id = source.Id,
        InheritsFrom = null,
        MainClass = source.MainClass,
        MinecraftArguments = source.MinecraftArguments,
        Arguments = source.Arguments,
        Libraries = source.Libraries,
        AssetIndex = source.AssetIndex,
        Assets = source.Assets,
        JavaVersion = source.JavaVersion,
        Downloads = source.Downloads,
        Logging = source.Logging,
        Type = source.Type,
        ReleaseTime = source.ReleaseTime,
        Time = source.Time,
        MinimumLauncherVersion = source.MinimumLauncherVersion,
        ComplianceLevel = source.ComplianceLevel
    };
}
