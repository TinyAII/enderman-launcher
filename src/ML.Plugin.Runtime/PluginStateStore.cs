using System.Text.Json;
using System.Text.Json.Serialization;

namespace ML.Plugin.Runtime;

/// <summary>
/// 记住哪些插件被用户停用了。
/// </summary>
/// <remarks>
/// 只存"偏离默认值的那些"：默认全启用，所以停用的才需要落盘。
/// 这样新装插件不需要迁移，删了插件也不会留垃圾。
/// </remarks>
internal sealed class PluginStateStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _path;
    private readonly Dictionary<string, bool> _enabled = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _grants = new(StringComparer.OrdinalIgnoreCase);

    public PluginStateStore(string path)
    {
        _path = path;
        Load();
    }

    public bool IsEnabled(string id) => !_enabled.TryGetValue(id, out bool value) || value;

    public void Set(string id, bool enabled)
    {
        _enabled[id] = enabled;
        Save();
    }

    /// <summary>这个插件被同意过哪些权限。</summary>
    public IReadOnlySet<string> Grants(string id) =>
        _grants.TryGetValue(id, out HashSet<string>? set)
            ? set
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public void Grant(string id, IEnumerable<string> permissions)
    {
        var set = new HashSet<string>(Grants(id), StringComparer.OrdinalIgnoreCase);

        foreach (string permission in permissions)
        {
            set.Add(permission);
        }

        _grants[id] = set;
        Save();
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            StateDocument? document = JsonSerializer.Deserialize<StateDocument>(File.ReadAllText(_path), Options);
            if (document?.Enabled is null)
            {
                return;
            }

            foreach ((string id, bool enabled) in document.Enabled)
            {
                _enabled[id] = enabled;
            }

            if (document.Grants is not null)
            {
                foreach ((string id, List<string> permissions) in document.Grants)
                {
                    _grants[id] = new HashSet<string>(permissions, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // 坏文件不删：用户手动改坏的时候，原文件还有救。
            // 这一次就当"全部默认启用"跑，不阻断启动。
            _enabled.Clear();
        }
    }

    private void Save()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var document = new StateDocument
            {
                Version = 1,
                Enabled = new Dictionary<string, bool>(_enabled, StringComparer.OrdinalIgnoreCase),
                Grants = _grants.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
                    StringComparer.OrdinalIgnoreCase)
            };

            File.WriteAllText(_path, JsonSerializer.Serialize(document, Options));
        }
        catch (IOException)
        {
            // 存不下就存不下：插件仍然按当前内存状态工作，不该因为写文件失败而崩。
        }
    }

    private sealed class StateDocument
    {
        public int Version { get; set; }

        public Dictionary<string, bool>? Enabled { get; set; }

        /// <summary>插件 id → 用户同意过的权限。没同意的权限不会被授予。</summary>
        public Dictionary<string, List<string>>? Grants { get; set; }
    }
}
