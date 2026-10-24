using ML.Core.Install;
using ML.Core.Rules;
using ML.Core.Versioning;

namespace ML.Core.Launch;

/// <summary>装配一次启动需要的全部输入。</summary>
public sealed record LaunchOptions
{
    public required GamePaths Paths { get; init; }

    /// <summary>合并后的版本元数据（继承链已展开、库已去重）。</summary>
    public required VersionMetadata Version { get; init; }

    public required EnvironmentProfile Environment { get; init; }

    /// <summary><c>java.exe</c> 的完整路径，由 Java 探测模块给出。</summary>
    public required string JavaExecutable { get; init; }

    /// <summary>游戏工作目录（<c>--gameDir</c>），存档与模组都在这里。</summary>
    public required string GameDirectory { get; init; }

    public required LaunchAccount Account { get; init; }

    public string LauncherName { get; init; } = "ML";

    public string LauncherVersion { get; init; } = "0.1.0";

    /// <summary>堆上限 MB。&lt;= 0 表示不加 <c>-Xmx</c>。</summary>
    public int MaxMemoryMb { get; init; } = 2048;

    /// <summary>
    /// log4j 配置文件的落盘路径。为 null 时不传 <c>-Dlog4j.configurationFile</c>，
    /// 游戏会退回内置默认配置（能跑，但缺 Mojang 的日志格式与压缩设定）。
    /// </summary>
    public string? Log4jConfigPath { get; init; }

    public IReadOnlyList<string> ExtraJvmArguments { get; init; } = [];

    public IReadOnlyList<string> ExtraGameArguments { get; init; } = [];

    /// <summary>窗口尺寸。为 null 时不传 <c>--width/--height</c>，游戏用默认值。</summary>
    public (int Width, int Height)? WindowSize { get; init; }
}

/// <summary>可以直接交给 <see cref="GameProcessRunner"/> 的启动命令。</summary>
public sealed record LaunchCommand
{
    public required string JavaExecutable { get; init; }

    /// <summary>完整参数列表（不含 exe 自身）。</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    public required string WorkingDirectory { get; init; }

    public required IReadOnlyList<string> Classpath { get; init; }

    /// <summary>classpath 里指向不存在文件的条目。非空就别起 JVM——它只会报 NoClassDefFoundError。</summary>
    public IReadOnlyList<string> MissingClasspathFiles { get; init; } = [];

    /// <summary>未能替换的占位符。非空说明元数据里有我们不认识的占位符，必须显式暴露。</summary>
    public IReadOnlyList<string> UnresolvedPlaceholders { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>只用于日志与诊断的完整命令行（不做任何 shell 转义，别拿来直接执行）。</summary>
    public string Describe()
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(JavaExecutable);
        foreach (var argument in Arguments)
        {
            builder.Append(' ');
            builder.Append(argument.Contains(' ', StringComparison.Ordinal) ? $"\"{argument}\"" : argument);
        }

        return builder.ToString();
    }
}

/// <summary>
/// 把元数据 + 账号 + 目录布局拼成启动命令。
/// </summary>
public static partial class LaunchCommandBuilder
{
    /// <summary>
    /// 官方启动器认得、但我们不主动填充的占位符：它们对应的功能（快速进入世界、
    /// 指定窗口尺寸）没实现时，对应参数必须<b>整条丢掉</b>。
    /// 留着会变成 <c>--width ""</c>，游戏解析数字时直接抛异常。
    /// </summary>
    private static readonly HashSet<string> OptionalPlaceholders = new(StringComparer.Ordinal)
    {
        "quickPlayMultiplayer",
        "quickPlayPath",
        "quickPlayRealms",
        "quickPlaySingleplayer",
        "resolution_width",
        "resolution_height"
    };

    public static LaunchCommand Build(LaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Paths);
        ArgumentNullException.ThrowIfNull(options.Version);
        ArgumentNullException.ThrowIfNull(options.Environment);
        ArgumentNullException.ThrowIfNull(options.Account);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.JavaExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.GameDirectory);

        string versionId = options.Version.Id
            ?? throw new InvalidOperationException("版本元数据缺少 id，无法装配启动参数。");

        var warnings = new List<string>();
        var unresolved = new List<string>();

        var classpath = ClasspathBuilder.Build(
            options.Paths,
            options.Version.Libraries,
            options.Environment,
            versionId);
        warnings.AddRange(classpath.Warnings);

        var placeholders = BuildPlaceholders(options, versionId, classpath, warnings);

        var environment = WithDerivedFeatures(options.Environment, options.WindowSize is not null);

        var jvm = new List<string>();

        if (options.MaxMemoryMb > 0)
        {
            jvm.Add($"-Xmx{options.MaxMemoryMb}m");
        }

        jvm.AddRange(options.ExtraJvmArguments);

        if (options.Version.Arguments?.Jvm is { Count: > 0 } jvmEntries)
        {
            ExpandInto(jvm, unresolved, jvmEntries, environment, placeholders);
        }
        else
        {
            // 1.12 及更早的元数据没有 arguments.jvm，只能自己补这两条。
            jvm.Add($"-Djava.library.path={options.Paths.Natives(versionId)}");
            jvm.Add("-cp");
            jvm.Add(classpath.AsArgument);
        }

        // log4j 配置参数不在 arguments.jvm 里，它挂在 logging.client.argument 下，
        // 官方启动器也是单独拼上去的。漏掉它游戏会退回内置默认配置：
        // 能跑，但日志格式与压缩设定都不是 Mojang 那份。
        // 调用方没给配置文件路径属于正常用法（不是错误），所以不报警告。
        if (options.Version.Logging?.Client?.Argument is { Length: > 0 } loggingArgument
            && !string.IsNullOrEmpty(options.Log4jConfigPath))
        {
            var expanded = ArgumentExpander.Expand(
                [new ArgumentEntry { Rules = null, Values = [loggingArgument] }],
                environment,
                placeholders);
            jvm.AddRange(expanded.Arguments);
            unresolved.AddRange(expanded.UnresolvedPlaceholders);
        }

        string? mainClass = options.Version.MainClass;
        if (string.IsNullOrWhiteSpace(mainClass))
        {
            throw new InvalidOperationException($"版本 {versionId} 缺少 mainClass，无法启动。");
        }

        var game = new List<string> { mainClass };

        if (options.Version.Arguments?.Game is { Count: > 0 } gameEntries)
        {
            ExpandInto(game, unresolved, gameEntries, environment, placeholders);
        }
        else
        {
            var legacy = ArgumentExpander.ExpandLegacy(options.Version.MinecraftArguments, placeholders);
            game.AddRange(legacy.Arguments);
            unresolved.AddRange(legacy.UnresolvedPlaceholders);

            if (legacy.Arguments.Count == 0)
            {
                warnings.Add("版本既没有 arguments.game 也没有 minecraftArguments，游戏参数只剩 mainClass。");
            }
        }

        game.AddRange(options.ExtraGameArguments);

        var arguments = new List<string>(jvm.Count + game.Count);
        arguments.AddRange(jvm);
        arguments.AddRange(game);

        return new LaunchCommand
        {
            JavaExecutable = options.JavaExecutable,
            Arguments = arguments,
            WorkingDirectory = options.GameDirectory,
            Classpath = classpath.Entries,
            MissingClasspathFiles = classpath.MissingFiles,
            UnresolvedPlaceholders = Deduplicate(unresolved),
            Warnings = warnings,
        };
    }

    /// <summary>24 个官方占位符的取值来源。缺一个就会在 <see cref="LaunchCommand.UnresolvedPlaceholders"/> 里露出来。</summary>
    private static Dictionary<string, string> BuildPlaceholders(
        LaunchOptions options,
        string versionId,
        ClasspathPlan classpath,
        List<string> warnings)
    {
        var paths = options.Paths;
        var account = options.Account;
        var version = options.Version;

        string assetIndexId = version.AssetIndex?.Id
            ?? version.Assets
            ?? "legacy";

        if (version.AssetIndex?.Id is null && version.Assets is null)
        {
            warnings.Add("版本没有 assetIndex 也没有 assets 字段，按 legacy 索引处理。");
        }

        var placeholders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["assets_index_name"] = assetIndexId,
            ["assets_root"] = paths.Assets,
            ["auth_access_token"] = account.AccessToken,
            ["auth_player_name"] = account.PlayerName,
            ["auth_uuid"] = account.Uuid,
            ["auth_xuid"] = account.Xuid ?? string.Empty,
            ["classpath"] = classpath.AsArgument,
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["clientid"] = account.ClientId ?? string.Empty,
            ["game_directory"] = options.GameDirectory,
            ["launcher_name"] = options.LauncherName,
            ["launcher_version"] = options.LauncherVersion,
            ["library_directory"] = paths.Libraries,
            ["natives_directory"] = paths.Natives(versionId),
            ["user_type"] = account.UserType,
            ["version_name"] = versionId,
            ["version_type"] = version.Type ?? "release",
        };

        // path 只被 logging 的 -Dlog4j.configurationFile=${path} 用到。
        if (!string.IsNullOrEmpty(options.Log4jConfigPath))
        {
            placeholders["path"] = options.Log4jConfigPath;
        }

        if (options.WindowSize is { } size)
        {
            placeholders["resolution_width"] = size.Width.ToString(System.Globalization.CultureInfo.InvariantCulture);
            placeholders["resolution_height"] = size.Height.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return placeholders;
    }

    /// <summary>
    /// 逐条展开：先按 rules 过滤，再整条丢掉"只由可选占位符构成"的参数项。
    /// 不用 <see cref="ArgumentExpander.Expand"/> 是因为它按条目批量处理，
    /// 拿不到"这一条该不该丢"的信息。
    /// </summary>
    private static void ExpandInto(
        List<string> into,
        List<string> unresolved,
        IEnumerable<ArgumentEntry> entries,
        EnvironmentProfile env,
        IReadOnlyDictionary<string, string> placeholders)
    {
        foreach (var entry in entries)
        {
            if (!RuleEvaluator.IsAllowed(entry.Rules, env))
            {
                continue;
            }

            if (IsOptionalOnly(entry, placeholders))
            {
                continue;
            }

            foreach (var value in entry.Values)
            {
                var expanded = ArgumentExpander.Expand(
                    [new ArgumentEntry { Rules = null, Values = [value] }],
                    env,
                    placeholders);

                into.AddRange(expanded.Arguments);
                unresolved.AddRange(expanded.UnresolvedPlaceholders);
            }
        }
    }

    private static bool IsOptionalOnly(ArgumentEntry entry, IReadOnlyDictionary<string, string> placeholders)
    {
        bool sawPlaceholder = false;

        foreach (var value in entry.Values)
        {
            foreach (System.Text.RegularExpressions.Match match in PlaceholderPattern().Matches(value))
            {
                string key = match.Groups[1].Value;

                if (!OptionalPlaceholders.Contains(key))
                {
                    return false;
                }

                sawPlaceholder = true;

                // 调用方给了值（比如窗口尺寸）就正常展开。
                if (placeholders.TryGetValue(key, out var provided) && provided.Length > 0)
                {
                    return false;
                }
            }
        }

        return sawPlaceholder;
    }

    /// <summary>
    /// 启动器自己决定的特性开关。<c>has_custom_resolution</c> 是官方元数据里
    /// <c>--width/--height</c> 那一条的守卫条件：不把它打开，就算给了窗口尺寸，
    /// 那四个参数也会被 rules 过滤掉（真机上踩过）。
    /// </summary>
    private static EnvironmentProfile WithDerivedFeatures(EnvironmentProfile env, bool hasCustomResolution)
    {
        var features = new Dictionary<string, bool>(env.Features, StringComparer.Ordinal)
        {
            ["has_custom_resolution"] = hasCustomResolution,
            ["is_demo_user"] = env.Features.TryGetValue("is_demo_user", out var demo) && demo,
            ["has_quick_plays_support"] = env.Features.TryGetValue("has_quick_plays_support", out var quickPlay) && quickPlay,
        };

        return new EnvironmentProfile
        {
            OsName = env.OsName,
            OsVersion = env.OsVersion,
            Arch = env.Arch,
            Features = features,
        };
    }

    private static List<string> Deduplicate(List<string> values)
    {
        if (values.Count == 0)
        {
            return values;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(values.Count);

        foreach (var value in values)
        {
            if (seen.Add(value))
            {
                result.Add(value);
            }
        }

        return result;
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"\$\{([A-Za-z0-9_]+)\}",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex PlaceholderPattern();
}
