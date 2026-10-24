using System.Text;
using System.Text.RegularExpressions;
using ML.Core.Rules;
using ML.Core.Versioning;

namespace ML.Core.Launch;

/// <summary>展开结果：最终参数列表 + 未能替换的占位符（用于诊断，而不是静默吞掉）。</summary>
public sealed record ExpandResult(
    IReadOnlyList<string> Arguments,
    IReadOnlyList<string> UnresolvedPlaceholders)
{
    public bool IsClean => UnresolvedPlaceholders.Count == 0;
}

/// <summary>
/// 按规则过滤并替换占位符，把 arguments.game / arguments.jvm 变成可直接交给进程的字符串。
/// </summary>
public static partial class ArgumentExpander
{
    /// <summary>
    /// 展开一组参数条目。
    /// </summary>
    /// <param name="entries">带 rules 的参数项；为 null 视为空。</param>
    /// <param name="env">用于求值条目自带规则的环境。</param>
    /// <param name="placeholders">占位符键值，键不带 <c>${}</c>。</param>
    public static ExpandResult Expand(
        IEnumerable<ArgumentEntry>? entries,
        EnvironmentProfile env,
        IReadOnlyDictionary<string, string> placeholders)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(placeholders);

        var args = new List<string>();
        var unresolved = new List<string>();

        if (entries is null)
        {
            return new ExpandResult(args, unresolved);
        }

        foreach (var entry in entries)
        {
            if (!RuleEvaluator.IsAllowed(entry.Rules, env))
            {
                continue;
            }

            foreach (var value in entry.Values)
            {
                args.Add(Replace(value, placeholders, unresolved));
            }
        }

        return new ExpandResult(args, unresolved);
    }

    /// <summary>
    /// 展开 1.12 及更早的 <c>minecraftArguments</c> 单串形式。
    /// 它不带 rules，直接按空格切分即可（原版参数里没有含空格的项）。
    /// </summary>
    public static ExpandResult ExpandLegacy(
        string? minecraftArguments,
        IReadOnlyDictionary<string, string> placeholders)
    {
        ArgumentNullException.ThrowIfNull(placeholders);

        var args = new List<string>();
        var unresolved = new List<string>();

        if (string.IsNullOrWhiteSpace(minecraftArguments))
        {
            return new ExpandResult(args, unresolved);
        }

        foreach (var token in minecraftArguments.Split(
                     ' ',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            args.Add(Replace(token, placeholders, unresolved));
        }

        return new ExpandResult(args, unresolved);
    }

    private static string Replace(
        string template,
        IReadOnlyDictionary<string, string> placeholders,
        List<string> unresolved)
    {
        if (!template.Contains("${", StringComparison.Ordinal))
        {
            return template;
        }

        return PlaceholderPattern().Replace(template, match =>
        {
            string key = match.Groups[1].Value;
            if (placeholders.TryGetValue(key, out var value))
            {
                return value;
            }

            unresolved.Add(key);
            return match.Value;
        });
    }

    /// <summary>
    /// 官方占位符形如 <c>${auth_player_name}</c>。这里只认 ASCII 标识符，
    /// 避免把参数里合法出现的 <c>${&lt;其它&gt;}</c> 误当占位符吞掉。
    /// </summary>
    [GeneratedRegex(@"\$\{([A-Za-z0-9_]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();
}
