using System.Text.Json;
using System.Text.Json.Serialization;

namespace ML.Features.Assets;

/// <summary>
/// <c>assets/indexes/{id}.json</c>。键是从 1.7 起的逻辑路径（<c>minecraft/sounds/...</c>），
/// 值只记哈希和大小——真实文件按哈希存在 <c>assets/objects</c> 里。
/// </summary>
public sealed class AssetIndex
{
    [JsonPropertyName("objects")]
    public Dictionary<string, AssetObject>? Objects { get; set; }

    /// <summary>
    /// 1.6 及更早：资源要按原名复制到 <c>assets/virtual/&lt;id&gt;/</c> 才能被游戏读到。
    /// </summary>
    [JsonPropertyName("virtual")]
    public bool Virtual { get; set; }

    /// <summary>
    /// 1.5.2 及更早：资源要摊平到游戏根目录的 <c>resources/</c> 下。
    /// </summary>
    [JsonPropertyName("map_to_resources")]
    public bool MapToResources { get; set; }

    public int Count => Objects?.Count ?? 0;

    /// <summary>是否需要把对象按原名摊出去（旧版本才需要）。</summary>
    public bool RequiresMaterialization => Virtual || MapToResources;

    public static AssetIndex Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return FromJson(File.ReadAllText(path));
    }

    public static AssetIndex FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var index = JsonSerializer.Deserialize<AssetIndex>(json);
        if (index is null)
        {
            throw new JsonException("资源索引解析结果为空");
        }

        return index;
    }
}

public sealed class AssetObject
{
    [JsonPropertyName("hash")]
    public string? Hash { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }
}
