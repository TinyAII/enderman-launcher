namespace ML.Core.Launch;

/// <summary>中文路径兼容的判定结果。</summary>
public enum LegacyPathMode
{
    /// <summary>不需要处理：Java 够新，或者路径本来就是纯 ASCII。</summary>
    NotNeeded,

    /// <summary>已经改成 ASCII 路径（通过目录联接）。</summary>
    Applied,

    /// <summary>需要处理但没成功，附带原因。</summary>
    Unavailable
}

public sealed record LegacyPathResult(
    string JavaExecutable,
    string GameDirectory,
    LegacyPathMode Mode,
    string? Reason = null);

/// <summary>
/// 「Java 18 以下 + 中文路径」的兼容处理。
/// </summary>
/// <remarks>
/// <para>
/// Java 18 以前的版本按系统 ANSI 代码页解码命令行，中文路径会变乱码、直接起不来。
/// PCL 的做法是外挂 wrapper 程序；我们不带那个 exe，改成把路径**映射到纯 ASCII 的目录联接**：
/// </para>
/// <code>
/// C:\Users\...\AppData\Local\ML\links\game-3f2a1b9c  →  D:\游戏\中文实例\game
/// </code>
/// <para>
/// 曾经试过 8.3 短路径，实测无效：短名只压长度，<c>中文探针</c> 会变成 <c>中文探~1</c>，仍然带中文。
/// </para>
/// </remarks>
public static class LegacyJavaPaths
{
    /// <summary>Java 18 起命令行按 UTF-8 处理，不再需要这个兼容。</summary>
    public const int FixedInMajor = 18;

    /// <param name="linksRoot">放链接的目录，必须是纯 ASCII 路径（一般是数据目录下的 links）。</param>
    public static LegacyPathResult Resolve(
        string javaExecutable,
        string gameDirectory,
        int javaMajor,
        bool compatibilityEnabled,
        string? linksRoot)
    {
        if (!compatibilityEnabled || javaMajor >= FixedInMajor)
        {
            return new LegacyPathResult(javaExecutable, gameDirectory, LegacyPathMode.NotNeeded);
        }

        if (ShortPath.IsAscii(javaExecutable) && ShortPath.IsAscii(gameDirectory))
        {
            return new LegacyPathResult(javaExecutable, gameDirectory, LegacyPathMode.NotNeeded);
        }

        if (string.IsNullOrWhiteSpace(linksRoot) || !ShortPath.IsAscii(linksRoot))
        {
            return new LegacyPathResult(
                javaExecutable,
                gameDirectory,
                LegacyPathMode.Unavailable,
                "链接目录不是纯 ASCII 路径，没法用来绕开中文路径");
        }

        string java = javaExecutable;
        string directory = gameDirectory;

        // 1) java 路径含中文：链接整个 JDK 根目录（java.exe 在 <home>\bin 下）
        if (!ShortPath.IsAscii(javaExecutable))
        {
            string? javaHome = Path.GetDirectoryName(Path.GetDirectoryName(javaExecutable));

            if (javaHome is null)
            {
                return new LegacyPathResult(javaExecutable, gameDirectory, LegacyPathMode.Unavailable, "解析不出 Java 根目录");
            }

            string link = Path.Combine(linksRoot, $"jdk-{Junction.Tag(javaHome)}");

            if (!Junction.Ensure(link, javaHome, out string? error))
            {
                return new LegacyPathResult(javaExecutable, gameDirectory, LegacyPathMode.Unavailable, error);
            }

            java = Path.Combine(link, "bin", "java.exe");
        }

        // 2) 游戏目录含中文：链接整个 game 目录（存档、mods 都从这里过）
        if (!ShortPath.IsAscii(gameDirectory))
        {
            string link = Path.Combine(linksRoot, $"game-{Junction.Tag(gameDirectory)}");

            if (!Junction.Ensure(link, gameDirectory, out string? error))
            {
                return new LegacyPathResult(javaExecutable, gameDirectory, LegacyPathMode.Unavailable, error);
            }

            directory = link;
        }

        bool ok = ShortPath.IsAscii(java) && ShortPath.IsAscii(directory);

        return new LegacyPathResult(
            java,
            directory,
            ok ? LegacyPathMode.Applied : LegacyPathMode.Unavailable,
            ok ? null : "链接建成了但路径仍含非 ASCII");
    }
}
