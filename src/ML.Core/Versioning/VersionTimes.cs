using System.Diagnostics.CodeAnalysis;

namespace ML.Core.Versioning;

/// <summary>
/// version.json 里时间字段的宽松解析。
/// 保留这种宽松是因为实测发现格式不统一：Mojang 用 <c>2023-06-12T13:25:51+00:00</c>，
/// Fabric 用 <c>2026-10-20T09:54:00+0000</c>，后者会让 <c>DateTimeOffset</c> 的严格转换器抛异常。
/// </summary>
public static class VersionTimes
{
    /// <summary>
    /// 尝试解析时间字段。解析不出来返回 <see langword="false"/>，
    /// 让调用方降级显示原始字符串，而不是让解析异常往上炸。
    /// </summary>
    public static bool TryParse(string? raw, out DateTimeOffset value)
    {
        value = default;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (DateTimeOffset.TryParse(
                raw,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>解析成功返回时间，否则返回 <see langword="null"/>。</summary>
    public static DateTimeOffset? ParseOrNull(string? raw) =>
        TryParse(raw, out var value) ? value : null;
}
