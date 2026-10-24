using System.Text.Json;

namespace ML.Plugin.Runtime;

/// <summary>
/// 插件清单。字段含义见 <c>docs/01-插件系统设计.md</c> 3.1。
/// </summary>
public sealed record PluginManifest
{
    public required int SchemaVersion { get; init; }

    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Version { get; init; }

    public required string ApiVersion { get; init; }

    public string? Description { get; init; }

    /// <summary><c>managed</c>（Tier2，程序集）或 <c>declarative</c>（Tier1，只写参数）。</summary>
    public required string Type { get; init; }

    /// <summary>
    /// 插件自己的头像（插件目录内的相对路径）。不给就用名字首字生成的方块。
    /// </summary>
    /// <remarks>
    /// 这是"插件长什么样由插件自己决定"的入口：头像、名字、描述都在清单里，
    /// 宿主只负责照着画，不在界面上下判断。
    /// </remarks>
    public string? Icon { get; init; }

    public string? Entry { get; init; }

    public string? EntryType { get; init; }

    public IReadOnlyList<string> Permissions { get; init; } = [];

    public IReadOnlyDictionary<string, string> Dependencies { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> LoadAfter { get; init; } = [];

    public IReadOnlyList<string> LoadBefore { get; init; } = [];

    public IReadOnlyList<string> Conflicts { get; init; } = [];

    public string? MinHostVersion { get; init; }

    /// <summary>Tier1 专用：启动时追加的 JVM 参数。</summary>
    public IReadOnlyList<string> JvmArguments { get; init; } = [];

    /// <summary>Tier1 专用：启动时追加的游戏参数。</summary>
    public IReadOnlyList<string> GameArguments { get; init; } = [];

    /// <summary>界面贡献（页面注入）：往预设插槽里挂 HTML 片段，可带一段脚本。</summary>
    public IReadOnlyList<PluginUiContribution> Ui { get; init; } = [];

    public bool IsDeclarative => string.Equals(Type, "declarative", StringComparison.OrdinalIgnoreCase);
}

/// <summary>清单解析结果：成不成、为什么不成。两者都要能显示给用户看。</summary>
/// <param name="Replaces">
/// 要顶掉的宿主控件 id（比如 <c>open-new-instance</c>）。
/// 挂载时宿主把这些控件藏起来，插件停用/卸载时还回去——
/// 这是"插件接管某个入口"的唯一正当方式：宿主知道谁换了什么，才能正确还原。
/// </param>
public sealed record PluginUiContribution(
    string Slot,
    string HtmlPath,
    string? ScriptPath,
    string? Title = null,
    string? IconPath = null,
    IReadOnlyList<string>? Replaces = null);

/// <summary>
/// 允许挂载的界面插槽。名字写死在这里，页面里的容器也用同一批名字，
/// 两边对不上就是"插件说有界面但看不见"——这种问题最难查，所以清单校验就拦。
/// </summary>
public static class PluginUiSlots
{
    public const string AccountBeforeList = "account.before-list";
    public const string InstancesToolbar = "instances.toolbar";

    /// <summary>
    /// 启动层：在所有页面显示之前铺满整个窗口。启动动画、启动密码这类插件挂这里。
    /// </summary>
    /// <remarks>
    /// 同一时间只挂一个（多个插件都声明时只认第一个，其余记警告）——
    /// 两层全屏盖在一起，用户只会看到最上面那层，下面那个永远没人知道。
    /// </remarks>
    public const string Startup = "startup";

    /// <summary>整页：插件在侧栏拿到一个自己的入口，点进去是独立页面（下载器、市场都靠它）。</summary>
    public const string Page = "page";

    /// <summary>设置：插件页里点开某一行后，弹窗里那块属于这个插件的设置区。</summary>
    public const string Settings = "settings";

    public static readonly IReadOnlyList<string> All = [AccountBeforeList, InstancesToolbar, Startup, Page, Settings];

    public static bool IsKnown(string slot) =>
        All.Contains(slot, StringComparer.OrdinalIgnoreCase);
}

public sealed record ManifestParseResult(
    PluginManifest? Manifest,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public static class PluginManifestParser
{
    public const int SupportedSchemaVersion = 1;

    /// <summary>宿主提供的契约版本。插件的 <c>apiVersion</c> 主版本必须等于它。</summary>
    public const string ContractVersion = "1.2";

    public static ManifestParseResult Parse(string json, string folderName)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return new ManifestParseResult(
                null,
                [$"清单不是合法 JSON（第 {ex.LineNumber + 1} 行）：{ex.Message}"],
                []);
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return new ManifestParseResult(null, ["清单根节点必须是对象"], []);
            }

            int schemaVersion = ReadInt(root, "schemaVersion") ?? 0;
            if (schemaVersion != SupportedSchemaVersion)
            {
                errors.Add($"schemaVersion 必须是 {SupportedSchemaVersion}，现在是 {schemaVersion}");
            }

            string? id = ReadString(root, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                errors.Add("缺少 id（反向域名，例如 com.example.hello）");
            }
            else if (!string.Equals(id, folderName, StringComparison.OrdinalIgnoreCase))
            {
                // 不拦，但要说：目录名和 id 不一致时，用户改目录名就会"插件消失"，很难查。
                warnings.Add($"目录名（{folderName}）和 id（{id}）不一致，建议改成一致");
            }

            string? name = ReadString(root, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                warnings.Add("缺少 name，列表里将显示 id");
            }

            string? version = ReadString(root, "version");
            if (string.IsNullOrWhiteSpace(version))
            {
                errors.Add("缺少 version（例如 1.0.0）");
            }

            string? apiVersion = ReadString(root, "apiVersion");
            if (string.IsNullOrWhiteSpace(apiVersion))
            {
                errors.Add("缺少 apiVersion（宿主契约版本，当前 1.0）");
            }
            else if (!ApiVersionMatches(apiVersion, errors))
            {
                // ApiVersionMatches 已经把原因写进 errors
            }

            string type = (ReadString(root, "type") ?? "managed").ToLowerInvariant();
            if (type is not ("managed" or "declarative"))
            {
                errors.Add($"type 只能是 managed 或 declarative，现在是 {type}");
            }

            string? entry = ReadString(root, "entry");
            string? entryType = ReadString(root, "entryType");

            if (type == "managed")
            {
                if (string.IsNullOrWhiteSpace(entry))
                {
                    errors.Add("managed 插件必须给 entry（相对插件目录的 dll 路径）");
                }
                else if (Path.IsPathRooted(entry) || entry.Contains("..", StringComparison.Ordinal))
                {
                    // 程序集路径逃出插件目录 = 能加载任意位置的 dll，这条不能松。
                    errors.Add("entry 不能是绝对路径，也不能包含 ..");
                }

                if (string.IsNullOrWhiteSpace(entryType))
                {
                    errors.Add("managed 插件必须给 entryType（实现 IPlugin 的完整类型名）");
                }
            }

            List<string> jvmArguments = [];
            List<string> gameArguments = [];

            if (root.TryGetProperty("launch", out JsonElement launch) && launch.ValueKind == JsonValueKind.Object)
            {
                jvmArguments = ReadStringArray(launch, "jvmArgs");
                gameArguments = ReadStringArray(launch, "gameArgs");
            }

            var uiContributions = new List<PluginUiContribution>();

            if (root.TryGetProperty("ui", out JsonElement ui) && ui.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in ui.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    string? slot = ReadString(item, "slot");
                    string? htmlPath = ReadString(item, "html");
                    string? scriptPath = ReadString(item, "script");
                    string? title = ReadString(item, "title");
                    string? iconPath = ReadString(item, "icon");

                    if (string.IsNullOrWhiteSpace(slot) || string.IsNullOrWhiteSpace(htmlPath))
                    {
                        errors.Add("ui 条目必须同时给 slot 和 html");
                        continue;
                    }

                    if (!PluginUiSlots.IsKnown(slot))
                    {
                        // 插槽名写错就不挂：挂到一个不存在的地方等于静默失效，更难查。
                        errors.Add($"未知的界面插槽 {slot}（可用：{string.Join(" / ", PluginUiSlots.All)}）");
                        continue;
                    }

                    if (IsUnsafeRelative(htmlPath)
                        || (scriptPath is not null && IsUnsafeRelative(scriptPath))
                        || (iconPath is not null && IsUnsafeRelative(iconPath)))
                    {
                        errors.Add("ui 里的 html / script / icon 必须是插件目录内的相对路径");
                        continue;
                    }

                    // 整页没有标题就是侧栏上一个空白按钮，用户根本不知道点开是什么。
                    if (PluginUiSlots.Page.Equals(slot, StringComparison.OrdinalIgnoreCase)
                        && string.IsNullOrWhiteSpace(title))
                    {
                        errors.Add("整页（slot=page）必须给 title");
                        continue;
                    }

                    List<string> replaces = [];

                    if (item.TryGetProperty("replaces", out JsonElement replacesElement)
                        && replacesElement.ValueKind == JsonValueKind.Array)
                    {
                        replaces.AddRange(replacesElement.EnumerateArray()
                            .Where(entry => entry.ValueKind == JsonValueKind.String)
                            .Select(entry => entry.GetString()!)
                            .Where(id => !string.IsNullOrWhiteSpace(id)));
                    }

                    uiContributions.Add(new PluginUiContribution(
                        slot, htmlPath, scriptPath, title, iconPath, replaces.Count > 0 ? replaces : null));
                }
            }

            if (type == "declarative" && jvmArguments.Count == 0 && gameArguments.Count == 0
                && uiContributions.Count == 0)
            {
                warnings.Add("declarative 插件没有声明任何 launch.jvmArgs / gameArgs，等于什么也不做");
            }

            if (errors.Count > 0)
            {
                return new ManifestParseResult(null, errors, warnings);
            }

            var manifest = new PluginManifest
            {
                SchemaVersion = schemaVersion,
                Id = id!,
                Name = string.IsNullOrWhiteSpace(name) ? id! : name!,
                Version = version!,
                ApiVersion = apiVersion!,
                Description = ReadString(root, "description"),
                Icon = ReadIcon(root, warnings),
                Type = type,
                Entry = entry,
                EntryType = entryType,
                Permissions = ReadStringArray(root, "permissions"),
                Dependencies = ReadStringMap(root, "dependencies"),
                LoadAfter = ReadStringArray(root, "loadAfter"),
                LoadBefore = ReadStringArray(root, "loadBefore"),
                Conflicts = ReadStringArray(root, "conflicts"),
                MinHostVersion = ReadString(root, "minHostVersion"),
                JvmArguments = jvmArguments,
                GameArguments = gameArguments,
                Ui = uiContributions
            };

            return new ManifestParseResult(manifest, [], warnings);
        }
    }

    /// <summary>读 icon，顺手挡掉绝对路径和 ..（否则插件能借这个字段读任意文件）。</summary>
    private static string? ReadIcon(JsonElement root, List<string> warnings)
    {
        string? icon = ReadString(root, "icon");

        if (string.IsNullOrWhiteSpace(icon))
        {
            return null;
        }

        if (Path.IsPathRooted(icon) || icon.Contains("..", StringComparison.Ordinal))
        {
            warnings.Add("icon 必须是插件目录内的相对路径，这个值被忽略了");
            return null;
        }

        return icon;
    }

    /// <summary>主版本必须相等，次版本插件不得高于宿主。</summary>
    private static bool ApiVersionMatches(string apiVersion, List<string> errors)
    {
        if (!TryParseVersion(apiVersion, out int major, out int minor))
        {
            errors.Add($"apiVersion 格式不对（{apiVersion}），应形如 1.0");
            return false;
        }

        if (!TryParseVersion(ContractVersion, out int hostMajor, out int hostMinor))
        {
            errors.Add("宿主契约版本号写错了，这是启动器自己的 bug");
            return false;
        }

        if (major != hostMajor)
        {
            errors.Add($"apiVersion 主版本不匹配：插件是 {apiVersion}，宿主只支持 {hostMajor}.x");
            return false;
        }

        if (minor > hostMinor)
        {
            errors.Add($"插件要求的 apiVersion {apiVersion} 比宿主提供的 {ContractVersion} 新，升级启动器再试");
            return false;
        }

        return true;
    }

    public static bool TryParseVersion(string text, out int major, out int minor)
    {
        major = 0;
        minor = 0;

        string[] parts = text.Split('.');
        if (parts.Length == 0 || !int.TryParse(parts[0], out major))
        {
            return false;
        }

        if (parts.Length > 1 && !int.TryParse(parts[1], out minor))
        {
            return false;
        }

        return true;
    }

    private static bool IsUnsafeRelative(string path) =>
        Path.IsPathRooted(path) || path.Contains("..", StringComparison.Ordinal);

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int parsed)
            ? parsed
            : null;

    private static List<string> ReadStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var items = new List<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
            {
                items.Add(text);
            }
        }

        return items;
    }

    private static Dictionary<string, string> ReadStringMap(JsonElement element, string name)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
        {
            return map;
        }

        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } text)
            {
                map[property.Name] = text;
            }
        }

        return map;
    }
}
