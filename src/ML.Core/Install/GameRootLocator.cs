namespace ML.Core.Install;

/// <summary>一个候选的游戏目录（<c>.minecraft</c>）。</summary>
/// <param name="Path">绝对路径。</param>
/// <param name="Source">从哪儿发现的，用于在界面上解释「为什么我推荐这个」。</param>
/// <param name="LooksValid">像不像一个真的游戏目录（有 <c>versions/&lt;id&gt;/&lt;id&gt;.json</c> 之类）。</param>
/// <param name="VersionCount">看起来能用的版本数量。</param>
public sealed record GameRootCandidate(string Path, string Source, bool LooksValid, int VersionCount);

/// <summary>
/// 找本机的游戏目录。**只读磁盘、不联网、不修改任何东西**——它只负责回答
/// 「这台机器上已经有哪些 <c>.minecraft</c>」，选哪个由用户决定。
/// </summary>
/// <remarks>
/// 之所以要「发现」而不是「让用户自己填路径」：这个启动器的定位是「启动已装好的版本」，
/// 用户机器上几乎一定已经有 <c>%APPDATA%\.minecraft</c>。首次启动直接扫出来，
/// 用户点一下就能用；填路径作为兜底手段保留。
/// </remarks>
public static class GameRootLocator
{
    /// <summary>允许用户显式指定游戏目录的环境变量（优先于一切自动发现）。</summary>
    public const string GameDirEnvironmentVariable = "ML_GAME_DIR";

    /// <summary>扫描候选游戏目录，按优先级排序并去重。</summary>
    /// <param name="launcherDataRoot">启动器自己的数据目录，用来找便携式的 <c>data/.minecraft</c>。</param>
    /// <param name="environment">测试注入用；默认读进程环境变量。</param>
    public static IReadOnlyList<GameRootCandidate> Detect(
        string? launcherDataRoot = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var results = new List<GameRootCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Consider(string? path, string source)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string full;
            try
            {
                full = Path.GetFullPath(path.Trim());
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return;
            }

            if (!seen.Add(full))
            {
                return;
            }

            results.Add(new GameRootCandidate(full, source, LooksLikeGameRoot(full), CountVersions(full)));
        }

        Consider(Read(environment, GameDirEnvironmentVariable), "环境变量 " + GameDirEnvironmentVariable);
        Consider(Path.Combine(Read(environment, "APPDATA") ?? "", ".minecraft"), "系统默认位置");
        Consider(Read(environment, "USERPROFILE") is { Length: > 0 } home
            ? Path.Combine(home, "AppData", "Roaming", ".minecraft")
            : null, "系统默认位置");
        Consider(launcherDataRoot is { Length: > 0 } ? Path.Combine(launcherDataRoot, ".minecraft") : null, "启动器数据目录");

        return results;
    }

    /// <summary>挑一个最该被推荐的：看起来有效且版本最多的那个。</summary>
    public static GameRootCandidate? Best(IEnumerable<GameRootCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        return candidates
            .Where(candidate => candidate.LooksValid)
            .OrderByDescending(candidate => candidate.VersionCount)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>
    /// 判断一个目录像不像游戏目录。用三条弱证据，命中任一即可：
    /// 存在 <c>versions/</c> 下的 <c>&lt;id&gt;/&lt;id&gt;.json</c>、存在 <c>libraries/</c>、
    /// 或存在 <c>launcher_profiles.json</c>（官方启动器建的）。
    /// </summary>
    public static bool LooksLikeGameRoot(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        try
        {
            return CountVersions(directory) > 0
                || Directory.Exists(Path.Combine(directory, "libraries"))
                || File.Exists(Path.Combine(directory, "launcher_profiles.json"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 数出 <c>versions/</c> 下「目录名与 json 名一致」的版本数。
    /// 名字对不上说明目录是残的，这种版本启动时必然失败，不该计入。
    /// </summary>
    public static int CountVersions(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return 0;
        }

        try
        {
            string versions = Path.Combine(directory, "versions");

            if (!Directory.Exists(versions))
            {
                return 0;
            }

            int count = 0;

            foreach (string sub in Directory.EnumerateDirectories(versions))
            {
                string id = Path.GetFileName(sub);

                if (File.Exists(Path.Combine(sub, id + ".json")))
                {
                    count++;
                }
            }

            return count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string? Read(IReadOnlyDictionary<string, string?>? environment, string name)
    {
        if (environment is not null)
        {
            return environment.TryGetValue(name, out string? value) ? value : null;
        }

        return Environment.GetEnvironmentVariable(name);
    }
}
