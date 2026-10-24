using System.Net.Http.Json;
using System.Text.Json;
using ML.Core.Versioning;

namespace ML.Features.Mojang;

/// <summary>
/// 从 Mojang 拉版本清单和单个版本的 version.json。
/// </summary>
/// <remarks>
/// 模型（<see cref="VersionManifest"/>）早就在了，但一直没人调用它——
/// 版本列表只看本地 versions 目录，所以"电脑上没装过游戏"的人用不了这个启动器。
/// </remarks>
public sealed class VersionManifestClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public HttpClient Http { get; } = http;

    /// <summary>拉完整清单。缓存交给调用方决定——清单一天内基本不变。</summary>
    public async Task<VersionManifest> FetchAsync(CancellationToken cancellationToken = default)
    {
        VersionManifest? manifest = await Http
            .GetFromJsonAsync<VersionManifest>(VersionManifest.DefaultUrl, Json, cancellationToken)
            .ConfigureAwait(false);

        return manifest ?? throw new InvalidDataException("版本清单是空的。");
    }

    /// <summary>在清单里找某个版本。找不到返回 null。</summary>
    public async Task<VersionManifestEntry?> FindAsync(string versionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);

        VersionManifest manifest = await FetchAsync(cancellationToken).ConfigureAwait(false);

        return manifest.Versions.FirstOrDefault(entry =>
            string.Equals(entry.Id, versionId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>拉某个版本的 version.json 原文。</summary>
    public async Task<string> FetchVersionJsonAsync(VersionManifestEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrWhiteSpace(entry.Url))
        {
            throw new InvalidDataException($"{entry.Id} 在清单里没有 version.json 地址。");
        }

        return await Http.GetStringAsync(entry.Url, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>拉并解析成内核的版本模型。</summary>
    public async Task<VersionMetadata> FetchVersionMetadataAsync(
        VersionManifestEntry entry,
        CancellationToken cancellationToken = default)
    {
        string json = await FetchVersionJsonAsync(entry, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Deserialize<VersionMetadata>(json)
            ?? throw new InvalidDataException($"{entry.Id} 的 version.json 解析失败。");
    }
}
