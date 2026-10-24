using System.Text.Json;
using ML.Plugin.Abstractions;

namespace ML.Plugin.Runtime;

/// <summary>
/// 插件设置：一个插件一个 <c>settings.json</c>，字符串键值。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不让插件自己写文件：设置界面在页面里（浏览器上下文，没有 fs），
/// 而值要能被插件代码读到——只能由宿主替它存一份。这样两端读写的是同一份数据。
/// </para>
/// <para>
/// 值一律当字符串存，复杂结构插件自己 JSON 序列化。读失败当空，写失败只记录不抛——
/// 设置存不下不该让插件起不来。
/// </para>
/// </remarks>
public sealed class PluginSettingsStore : IPluginSettings
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public PluginSettingsStore(string path)
    {
        _path = path;
        Load();
    }

    public IReadOnlyDictionary<string, string?> All
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, string?>(_values, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public string? Get(string key)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out string? value) ? value : null;
        }
    }

    public void Set(string key, string? value)
    {
        lock (_gate)
        {
            if (value is null)
            {
                _values.Remove(key);
            }
            else
            {
                _values[key] = value;
            }

            Save();
        }
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            Dictionary<string, string?>? loaded =
                JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(_path));

            if (loaded is null)
            {
                return;
            }

            foreach ((string key, string? value) in loaded)
            {
                _values[key] = value;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // 文件坏了就当没设置：不删原文件，用户可能手改过、还有救。
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

            File.WriteAllText(_path, JsonSerializer.Serialize(_values, Options));
        }
        catch (IOException)
        {
            // 写不进去就算了，内存里的值这次仍然有效。
        }
    }
}
