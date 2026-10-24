using System.Text.Json;
using System.Text.Json.Serialization;
using ML.Core.Rules;

namespace ML.Core.Versioning;

/// <summary>
/// version.json 的原始映射。刻意保持「全字段可空、未识别字段丢弃」：
/// 官方、Forge、NeoForge、Fabric、整合包作者的 JSON 都往这里塞东西，
/// 解析层必须宽松，语义处理交给 <see cref="VersionMerger"/> 和后续解析器。
/// </summary>
public sealed class VersionMetadata
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("inheritsFrom")]
    public string? InheritsFrom { get; set; }

    [JsonPropertyName("mainClass")]
    public string? MainClass { get; set; }

    /// <summary>1.12 及更早：单个以空格分隔的参数字符串。</summary>
    [JsonPropertyName("minecraftArguments")]
    public string? MinecraftArguments { get; set; }

    /// <summary>1.13+：结构化的 game / jvm 参数表。</summary>
    [JsonPropertyName("arguments")]
    public ArgumentsBlock? Arguments { get; set; }

    [JsonPropertyName("libraries")]
    public List<LibraryEntry>? Libraries { get; set; }

    [JsonPropertyName("assetIndex")]
    public AssetIndexRef? AssetIndex { get; set; }

    [JsonPropertyName("assets")]
    public string? Assets { get; set; }

    [JsonPropertyName("javaVersion")]
    public JavaVersionRef? JavaVersion { get; set; }

    [JsonPropertyName("downloads")]
    public Dictionary<string, DownloadRef>? Downloads { get; set; }

    [JsonPropertyName("logging")]
    public LoggingConfig? Logging { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>
    /// 发布时间。**故意保留原始字符串**：Mojang 写 <c>+00:00</c>，Fabric 写 <c>+0000</c>，
    /// 整合包作者还可能写别的。用 <c>DateTimeOffset</c> 严格反序列化会让整个 version.json
    /// 解析失败——而启动器根本不靠它做任何计算，只用来显示「最后更新时间」。
    /// 需要时间值时用 <see cref="VersionTimes.TryParse"/>。
    /// </summary>
    [JsonPropertyName("releaseTime")]
    public string? ReleaseTime { get; set; }

    [JsonPropertyName("time")]
    public string? Time { get; set; }

    [JsonPropertyName("minimumLauncherVersion")]
    public int? MinimumLauncherVersion { get; set; }

    [JsonPropertyName("complianceLevel")]
    public int? ComplianceLevel { get; set; }
}

public sealed class ArgumentsBlock
{
    [JsonPropertyName("game")]
    public List<ArgumentEntry>? Game { get; set; }

    [JsonPropertyName("jvm")]
    public List<ArgumentEntry>? Jvm { get; set; }
}

/// <summary>
/// arguments 数组里的一项，可能是裸字符串，也可能是 <c>{ "rules": [...], "value": "..." }</c>。
/// </summary>
[JsonConverter(typeof(ArgumentEntryConverter))]
public sealed class ArgumentEntry
{
    public IReadOnlyList<Rule>? Rules { get; init; }

    public IReadOnlyList<string> Values { get; init; } = [];

    public static ArgumentEntry Plain(string value) =>
        new() { Values = [value] };
}

internal sealed class ArgumentEntryConverter : JsonConverter<ArgumentEntry>
{
    public override ArgumentEntry Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return new ArgumentEntry { Values = [reader.GetString() ?? string.Empty] };

            case JsonTokenType.StartObject:
            {
                using var doc = JsonDocument.ParseValue(ref reader);
                var root = doc.RootElement;

                IReadOnlyList<Rule>? rules = null;
                if (root.TryGetProperty("rules", out var rulesEl))
                {
                    rules = rulesEl.Deserialize<List<Rule>>(options);
                }

                List<string> values = [];
                if (root.TryGetProperty("value", out var valueEl))
                {
                    if (valueEl.ValueKind == JsonValueKind.String)
                    {
                        values.Add(valueEl.GetString() ?? string.Empty);
                    }
                    else if (valueEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in valueEl.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String)
                            {
                                values.Add(item.GetString() ?? string.Empty);
                            }
                        }
                    }
                }

                return new ArgumentEntry { Rules = rules, Values = values };
            }

            default:
                throw new JsonException(
                    $"arguments 项既不是字符串也不是对象（实际 {reader.TokenType}）");
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        ArgumentEntry value,
        JsonSerializerOptions options)
    {
        if (value.Rules is null || value.Rules.Count == 0)
        {
            writer.WriteStartArray();
            foreach (var v in value.Values)
            {
                writer.WriteStringValue(v);
            }
            writer.WriteEndArray();
            return;
        }

        writer.WriteStartObject();
        writer.WriteStartArray("rules");
        foreach (var rule in value.Rules)
        {
            JsonSerializer.Serialize(writer, rule, options);
        }
        writer.WriteEndArray();

        if (value.Values.Count == 1)
        {
            writer.WriteString("value", value.Values[0]);
        }
        else
        {
            writer.WriteStartArray("value");
            foreach (var v in value.Values)
            {
                writer.WriteStringValue(v);
            }
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }
}

public sealed class LibraryEntry
{
    /// <summary>Maven 坐标字符串。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("downloads")]
    public LibraryDownloads? Downloads { get; set; }

    /// <summary>
    /// 1.18 及更早的 natives 映射：<c>{ "windows": "natives-windows-${arch}" }</c>。
    /// 1.19+ 改成了「库名自带 classifier」，这个字段就没有了——所以两种都要支持。
    /// </summary>
    [JsonPropertyName("natives")]
    public Dictionary<string, string>? Natives { get; set; }

    [JsonPropertyName("rules")]
    public List<Rule>? Rules { get; set; }

    /// <summary>Fabric/Quilt 等非 Mojang 格式使用的直链仓库前缀。</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("extract")]
    public ExtractRule? Extract { get; set; }

    /// <summary>Forge 1.12 时代的扁平写法。</summary>
    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }
}

public sealed class LibraryDownloads
{
    [JsonPropertyName("artifact")]
    public DownloadRef? Artifact { get; set; }

    /// <summary>natives 按平台分类的 jar，键名如 <c>natives-windows</c>。</summary>
    [JsonPropertyName("classifiers")]
    public Dictionary<string, DownloadRef>? Classifiers { get; set; }
}

public sealed class DownloadRef
{
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public sealed class ExtractRule
{
    [JsonPropertyName("exclude")]
    public List<string>? Exclude { get; set; }
}

public sealed class AssetIndexRef
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("totalSize")]
    public long? TotalSize { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public sealed class JavaVersionRef
{
    [JsonPropertyName("component")]
    public string? Component { get; set; }

    [JsonPropertyName("majorVersion")]
    public int? MajorVersion { get; set; }
}

public sealed class LoggingConfig
{
    [JsonPropertyName("client")]
    public LoggingClient? Client { get; set; }
}

public sealed class LoggingClient
{
    [JsonPropertyName("argument")]
    public string? Argument { get; set; }

    [JsonPropertyName("file")]
    public LoggingFile? File { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

public sealed class LoggingFile
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}
