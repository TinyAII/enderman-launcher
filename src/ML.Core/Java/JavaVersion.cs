using System.Globalization;

namespace ML.Core.Java;

/// <summary>
/// Java 版本号。<c>Raw</c> 保留原文，比较只看 <see cref="Major"/> 与数字分量。
/// 需要兼容两种历史写法：
/// <list type="bullet">
/// <item>1.8 及更早：<c>1.8.0_402</c> → 主版本 8</item>
/// <item>9 及以后：<c>21.0.10</c> → 主版本 21</item>
/// </list>
/// </summary>
public readonly record struct JavaVersion(int Major, string Raw) : IComparable<JavaVersion>
{
    public static readonly JavaVersion Unknown = new(0, "unknown");

    public bool IsKnown => Major > 0;

    public static bool TryParse(string? raw, out JavaVersion version)
    {
        version = Unknown;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var text = raw.Trim().Trim('"');
        var components = SplitNumericComponents(text);
        if (components.Count == 0)
        {
            return false;
        }

        // 1.x.0_n 是唯一的「主版本不在第一位」的格式。
        int major = components[0] == 1 && components.Count > 1 ? components[1] : components[0];
        if (major <= 0)
        {
            return false;
        }

        version = new JavaVersion(major, text);
        return true;
    }

    public int CompareTo(JavaVersion other)
    {
        if (Major != other.Major)
        {
            return Major.CompareTo(other.Major);
        }

        // 同一主版本内比数字分量：17.0.9 < 17.0.17（按字符串比会反）。
        var left = SplitNumericComponents(Raw);
        var right = SplitNumericComponents(other.Raw);

        for (var i = 0; i < Math.Max(left.Count, right.Count); i++)
        {
            var l = i < left.Count ? left[i] : 0;
            var r = i < right.Count ? right[i] : 0;
            if (l != r)
            {
                return l.CompareTo(r);
            }
        }

        return 0;
    }

    public override string ToString() => Raw;

    public static bool operator <(JavaVersion left, JavaVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(JavaVersion left, JavaVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(JavaVersion left, JavaVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(JavaVersion left, JavaVersion right) => left.CompareTo(right) >= 0;

    private static List<int> SplitNumericComponents(string text)
    {
        var result = new List<int>();
        var start = -1;

        for (var i = 0; i <= text.Length; i++)
        {
            var isDigit = i < text.Length && char.IsAsciiDigit(text[i]);

            if (isDigit && start < 0)
            {
                start = i;
            }
            else if (!isDigit && start >= 0)
            {
                if (int.TryParse(
                        text.AsSpan(start, i - start),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var value))
                {
                    result.Add(value);
                }

                start = -1;
            }
        }

        return result;
    }
}
