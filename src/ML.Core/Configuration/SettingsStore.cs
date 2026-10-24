using System.Text.Json;

namespace ML.Core.Configuration;

/// <summary>
/// 启动器设置的读写。文件位置固定为 <c>&lt;启动器数据目录&gt;/config.json</c>。
/// </summary>
/// <remarks>
/// 两条实现约定：
/// ① <b>写入必须原子</b>：先写 <c>config.json.tmp</c> 再 <c>File.Move(..., overwrite: true)</c>。
///    直接覆写时断电/崩溃会留下半截 JSON，下次启动读不出来，用户会以为「我的设置全丢了」。
/// ② <b>读不出来不等于要覆盖</b>：JSON 坏掉时把原文件改名成 <c>config.json.broken</c>
///    再回落到默认值。用户手改配置写错一个逗号，不该静默丢掉他所有的设置。
/// </remarks>
public sealed class SettingsStore
{
    private readonly string _dataDirectory;

    public SettingsStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _dataDirectory = Path.GetFullPath(dataDirectory);
        FilePath = Path.Combine(_dataDirectory, "config.json");
    }

    /// <summary>配置文件绝对路径。</summary>
    public string FilePath { get; }

    /// <summary>损坏配置的备份路径（只有真坏过才存在）。</summary>
    public string BrokenFilePath => FilePath + ".broken";

    /// <summary>上次 <see cref="Load"/> 或 <see cref="Save"/> 之后的有效值。</summary>
    public LauncherSettings Current { get; private set; } = new LauncherSettings();

    /// <summary>读盘。文件不存在、读不了、JSON 坏掉，都回落到默认值，不抛。</summary>
    public LauncherSettings Load()
    {
        Current = new LauncherSettings();

        try
        {
            if (!File.Exists(FilePath))
            {
                return Current;
            }

            string json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize(json, LauncherSettingsJsonContext.Default.LauncherSettings);

            if (loaded is not null)
            {
                Current = loaded.Normalize();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            QuarantineBrokenFile();
        }

        return Current;
    }

    /// <summary>原子写入。返回值是实际落盘的值（已 <see cref="LauncherSettings.Normalize"/> 过）。</summary>
    public LauncherSettings Save(LauncherSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        LauncherSettings normalized = settings.Normalize();
        Directory.CreateDirectory(_dataDirectory);

        string temp = FilePath + ".tmp";
        string json = JsonSerializer.Serialize(normalized, LauncherSettingsJsonContext.Default.LauncherSettings);

        File.WriteAllText(temp, json);
        File.Move(temp, FilePath, overwrite: true);

        Current = normalized;
        return normalized;
    }

    /// <summary>在现有值上改几个字段并落盘。</summary>
    public LauncherSettings Update(Func<LauncherSettings, LauncherSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        if (!File.Exists(FilePath))
        {
            Load();
        }

        return Save(mutate(Current));
    }

    private void QuarantineBrokenFile()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Move(FilePath, BrokenFilePath, overwrite: true);
            }
        }
        catch (IOException)
        {
            // 备份失败（例如文件被占用）也不能让启动失败：设置回落到默认值即可。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
