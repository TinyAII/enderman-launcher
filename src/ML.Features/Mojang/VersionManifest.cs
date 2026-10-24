using System.Text.Json;
using System.Text.Json.Serialization;
using ML.Core.Versioning;

namespace ML.Features.Mojang;

/// <summary>版本清单里的一个条目。</summary>
public sealed class VersionManifestEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary><c>release</c> / <c>snapshot</c> / <c>old_beta</c> / <c>old_alpha</c>。</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>该版本 version.json 的地址。</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("time")]
    public string? Time { get; set; }

    [JsonPropertyName("releaseTime")]
    public string? ReleaseTime { get; set; }

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    public bool IsRelease => string.Equals(Type, "release", StringComparison.OrdinalIgnoreCase);

    public bool IsSnapshot => string.Equals(Type, "snapshot", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Mojang 的版本清单 <c>version_manifest_v2.json</c>。
/// </summary>
public sealed class VersionManifest
{
    public const string DefaultUrl = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";

    [JsonPropertyName("latest")]
    public VersionManifestLatest? Latest { get; set; }

    [JsonPropertyName("versions")]
    public List<VersionManifestEntry> Versions { get; set; } = [];

    /// <summary>只要正式版，按清单顺序（新→旧）。</summary>
    public IReadOnlyList<VersionManifestEntry> Releases =>
        [.. Versions.Where(v => v.IsRelease)];

    public IReadOnlyList<VersionManifestEntry> Snapshots =>
        [.. Versions.Where(v => v.IsSnapshot)];

    public VersionManifestEntry? Find(string id) =>
        Versions.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 解析清单。时间字段一律当字符串留着——Mojang 写 <c>+00:00</c>、部分第三方源写 <c>+0000</c>，
    /// 强类型会让整份清单解析失败，而发布时间只用来显示。
    /// </summary>
    public static VersionManifest Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var manifest = JsonSerializer.Deserialize<VersionManifest>(json, SerializerOptions)
            ?? throw new InvalidDataException("版本清单是空内容。");

        return manifest;
    }

    /// <summary>取条目里的发布时间（宽松解析，坏值给 null 而不是抛）。</summary>
    public static DateTimeOffset? ReleaseTimeOf(VersionManifestEntry entry) =>
        VersionTimes.ParseOrNull(entry.ReleaseTime ?? entry.Time);

    /// <summary>拉取清单。</summary>
    public static async Task<VersionManifest> FetchAsync(
        HttpClient http,
        string url = DefaultUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);

        string json = await http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);

        return Parse(json);
    }

    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

public sealed class VersionManifestLatest
{
    [JsonPropertyName("release")]
    public string? Release { get; set; }

    [JsonPropertyName("snapshot")]
    public string? Snapshot { get; set; }
}
