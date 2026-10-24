using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace ML.Core.Rules;

/// <summary>
/// version.json 里的一条 rule。语义遵循官方启动器：按顺序求值，最后一个命中的规则决定结果。
/// </summary>
public sealed class Rule
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "disallow";

    [JsonPropertyName("os")]
    public OsCondition? Os { get; set; }

    [JsonPropertyName("features")]
    public Dictionary<string, bool>? Features { get; set; }

    [JsonIgnore]
    public bool IsAllow => string.Equals(Action, "allow", StringComparison.OrdinalIgnoreCase);
}

public sealed class OsCondition
{
    /// <summary>windows / osx / linux</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>正则，匹配 <see cref="EnvironmentProfile.OsVersion"/></summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>x86 / x86_64 / arm64</summary>
    [JsonPropertyName("arch")]
    public string? Arch { get; set; }
}

/// <summary>
/// 求值 rule 所需的运行环境描述。默认值由 <see cref="Detect"/> 从当前进程推断，
/// 但一律显式传入，便于伪造环境做单测与「按其它平台准备实例」。
/// </summary>
public sealed class EnvironmentProfile
{
    public const string OsWindows = "windows";
    public const string OsMacOs = "osx";
    public const string OsLinux = "linux";

    public required string OsName { get; init; }

    /// <summary>操作系统版本串，只被规则里的 os.version 正则使用。</summary>
    public string OsVersion { get; init; } = string.Empty;

    /// <summary>x86 / x86_64 / arm64</summary>
    public required string Arch { get; init; }

    /// <summary>
    /// 启动器侧特性开关（is_demo_user、has_custom_resolution 等）。
    /// 规则要求但这里没有的键按 false 处理——与官方启动器一致。
    /// </summary>
    public IReadOnlyDictionary<string, bool> Features { get; init; } =
        new Dictionary<string, bool>(0);

    /// <summary>按当前进程推断环境。仅供宿主调用；Core 内部所有算法都接受显式 EnvironmentProfile。</summary>
    public static EnvironmentProfile Detect(
        IReadOnlyDictionary<string, bool>? features = null)
    {
        string osName =
            OperatingSystem.IsWindows() ? OsWindows :
            OperatingSystem.IsMacOS() ? OsMacOs :
            OperatingSystem.IsLinux() ? OsLinux :
            "unknown";

        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.X64 => "x86_64",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm32",
            var other => other.ToString().ToLowerInvariant()
        };

        return new EnvironmentProfile
        {
            OsName = osName,
            OsVersion = Environment.OSVersion.Version.ToString(),
            Arch = arch,
            Features = features ?? new Dictionary<string, bool>(0)
        };
    }
}

public static class RuleEvaluator
{
    /// <summary>
    /// 判断一组规则是否放行。
    /// 无规则 → 放行；有规则 → 逆序找第一条命中的，没有命中则拒绝。
    /// </summary>
    public static bool IsAllowed(IReadOnlyList<Rule>? rules, EnvironmentProfile env)
    {
        ArgumentNullException.ThrowIfNull(env);

        if (rules is null || rules.Count == 0)
        {
            return true;
        }

        for (int i = rules.Count - 1; i >= 0; i--)
        {
            if (Matches(rules[i], env))
            {
                return rules[i].IsAllow;
            }
        }

        // 有规则但一条都没命中 → 默认拒绝（这是官方语义，不是笔误）
        return false;
    }

    /// <summary>单条规则的全部条件是否满足。</summary>
    public static bool Matches(Rule rule, EnvironmentProfile env)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(env);

        return MatchesOs(rule.Os, env) && MatchesFeatures(rule.Features, env);
    }

    private static bool MatchesOs(OsCondition? os, EnvironmentProfile env)
    {
        if (os is null)
        {
            return true;
        }

        if (os.Name is not null &&
            !string.Equals(os.Name, env.OsName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (os.Arch is not null &&
            !string.Equals(os.Arch, env.Arch, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (os.Version is not null && !VersionRegexMatches(os.Version, env.OsVersion))
        {
            return false;
        }

        return true;
    }

    private static bool MatchesFeatures(
        Dictionary<string, bool>? required,
        EnvironmentProfile env)
    {
        if (required is null || required.Count == 0)
        {
            return true;
        }

        foreach (var (key, expected) in required)
        {
            bool actual = env.Features.TryGetValue(key, out var v) && v;
            if (actual != expected)
            {
                return false;
            }
        }

        return true;
    }

    [SuppressMessage("Performance", "SYSLIB1045:Use GeneratedRegex",
        Justification = "模式来自 version.json 数据，编译期不可知。")]
    private static bool VersionRegexMatches(string pattern, string input)
    {
        try
        {
            return Regex.IsMatch(input, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(50));
        }
        catch (ArgumentException)
        {
            // 数据里的非法正则不应让整个启动流程炸掉：当作不匹配
            return false;
        }
    }
}
