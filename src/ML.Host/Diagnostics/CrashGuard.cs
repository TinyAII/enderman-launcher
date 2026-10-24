using System.Text.Json;
using System.Text.Json.Serialization;

namespace ML.Host.Diagnostics;

/// <summary>一次崩溃的现场。</summary>
public sealed record CrashReport(
    DateTimeOffset Time,
    string Version,
    string Source,
    string? ActivePlugin,
    string ExceptionType,
    string Message,
    string StackTrace,
    IReadOnlyList<string> LoadedPlugins,
    IReadOnlyList<string> RecentEvents)
{
    public string FilePath { get; init; } = string.Empty;
}

/// <summary>
/// 崩溃守护：记录"上次是不是崩的"，并在崩溃瞬间尽量把现场写盘。
/// </summary>
/// <remarks>
/// <para>
/// 策略是<b>不限制插件</b>——插件和宿主同进程、能碰任何东西，这是刻意的。
/// 既然拦不住，就保证"崩了之后能查、能一键恢复"：
/// 启动时写运行标记，正常退出删掉；标记还在就说明上次没善终。
/// </para>
/// <para>
/// 异常兜底的写盘时间是有限的（进程正在死），所以只写必要的东西：
/// 异常本身、当时在跑哪个插件、加载了哪些插件、内存里最后几百条事件。
/// </para>
/// </remarks>
public sealed class CrashGuard
{
    private const int RecentCapacity = 400;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _gate = new();
    private readonly LinkedList<string> _recent = new();
    private readonly string _markerPath;

    private string? _activePlugin;

    public CrashGuard(string dataRoot, string version)
    {
        Root = Path.Combine(dataRoot, "crash");
        System.IO.Directory.CreateDirectory(Root);

        Version = version;
        _markerPath = Path.Combine(Root, "running.json");
        PendingPath = Path.Combine(Root, "pending.json");
    }

    public string Root { get; }

    /// <summary>上次没善终的标记。正常退出会删掉。</summary>
    public string PendingPath { get; }

    public string Version { get; }

    /// <summary>当前正在执行哪个插件的代码。崩溃时靠它指认"谁干的"（尽力而为）。</summary>
    public string? ActivePlugin => _activePlugin;

    /// <summary>记一条事件到内存环。崩溃时一起写出去，相当于"最后几秒的日志"。</summary>
    public void Log(string line)
    {
        lock (_gate)
        {
            _recent.AddLast(line);

            while (_recent.Count > RecentCapacity)
            {
                _recent.RemoveFirst();
            }
        }
    }

    /// <summary>启动时调用：写下运行标记。下次启动发现它还在，就说明这次没善终。</summary>
    public void MarkRunning(IEnumerable<string> plugins)
    {
        try
        {
            // 先把上次的标记转存成 pending：否则本次启动会把它覆盖掉，
            // 前端再查就只剩"这次运行中"，崩溃现场就丢了（踩过）。
            if (File.Exists(_markerPath) && !File.Exists(PendingPath))
            {
                File.Copy(_markerPath, PendingPath);
            }

            var marker = new
            {
                time = DateTimeOffset.Now,
                pid = Environment.ProcessId,
                version = Version,
                plugins = plugins.ToArray(),
                activePlugin = _activePlugin
            };

            File.WriteAllText(_markerPath, JsonSerializer.Serialize(marker, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写不下标记不影响启动，只是这次崩溃会识别不到
        }
    }

    /// <summary>正常退出：删掉运行标记。删不掉也没关系，下次会当成功亏。</summary>
    public void MarkCleanExit()
    {
        try
        {
            if (File.Exists(_markerPath))
            {
                File.Delete(_markerPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 忽略
        }
    }

    /// <summary>进入插件代码时包一层，出问题能指认来源。</summary>
    public IDisposable EnterPlugin(string? pluginId)
    {
        string? previous = _activePlugin;
        _activePlugin = pluginId;

        return new Scope(this, previous);
    }

    /// <summary>记录崩溃现场。可以在异常未处理时调用——写盘时间有限，只写必要的。</summary>
    public CrashReport? Record(Exception? exception, string source)
    {
        try
        {
            string[] recent;

            lock (_gate)
            {
                recent = [.. _recent];
            }

            string[] plugins = ReadMarkerPlugins();

            var report = new CrashReport(
                DateTimeOffset.Now,
                Version,
                source,
                _activePlugin,
                exception?.GetType().FullName ?? "Unknown",
                exception?.Message ?? "(没有异常对象)",
                exception?.ToString() ?? "(没有堆栈)",
                plugins,
                recent)
            {
                FilePath = Path.Combine(Root, $"crash-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json")
            };

            File.WriteAllText(report.FilePath, JsonSerializer.Serialize(report, Options));

            // 标记里补上崩溃文件路径，下次启动直接能定位
            try
            {
                File.WriteAllText(_markerPath, JsonSerializer.Serialize(new
                {
                    time = report.Time,
                    pid = Environment.ProcessId,
                    version = Version,
                    plugins,
                    activePlugin = report.ActivePlugin,
                    crashFile = report.FilePath
                }, Options));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 标记写不进去就算了，crash-*.json 本身已经落盘
            }

            return report;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>启动时查：上次是不是崩的？有就返回报告。</summary>
    public CrashReport? ReadPending()
    {
        // pending.json 是"上次没善终"的留存；running.json 是这一次的运行标记。
        // 先看 pending，没有再看 running（程序刚崩、还没重启过时只有 running）。
        string sourcePath = File.Exists(PendingPath) ? PendingPath : _markerPath;

        if (!File.Exists(sourcePath))
        {
            return null;
        }

        try
        {
            using JsonDocument marker = JsonDocument.Parse(File.ReadAllText(sourcePath));
            JsonElement root = marker.RootElement;

            string? crashFile = root.TryGetProperty("crashFile", out JsonElement path) ? path.GetString() : null;

            if (crashFile is not null && File.Exists(crashFile))
            {
                CrashReport? saved = JsonSerializer.Deserialize<CrashReport>(File.ReadAllText(crashFile), Options);

                if (saved is not null)
                {
                    return saved with { FilePath = crashFile };
                }
            }

            // 标记还在但没有崩溃文件：多半是被任务管理器杀掉、或者崩在异常兜底之前
            DateTimeOffset time = root.TryGetProperty("time", out JsonElement when) && when.TryGetDateTimeOffset(out DateTimeOffset parsed)
                ? parsed
                : DateTimeOffset.Now;

            return new CrashReport(
                time,
                Version,
                "ProcessExit",
                root.TryGetProperty("activePlugin", out JsonElement active) ? active.GetString() : null,
                "AbnormalExit",
                "上次退出没有走正常流程（可能是被强制结束，或者崩在异常处理之前）。",
                string.Empty,
                ReadMarkerPlugins(),
                [])
            {
                FilePath = sourcePath
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void ClearPending()
    {
        try
        {
            foreach (string path in new[] { _markerPath, PendingPath })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 忽略
        }
    }

    /// <summary>崩溃目录里的历史文件（新→旧），设置页展示用。</summary>
    public IReadOnlyList<(string Path, DateTimeOffset Time, long Bytes)> ListCrashes()
    {
        if (!System.IO.Directory.Exists(Root))
        {
            return [];
        }

        return [.. new DirectoryInfo(Root)
            .GetFiles("crash-*.json")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => (file.FullName, new DateTimeOffset(file.LastWriteTimeUtc), file.Length))];
    }

    private string[] ReadMarkerPlugins()
    {
        try
        {
            if (!File.Exists(_markerPath))
            {
                return [];
            }

            using JsonDocument marker = JsonDocument.Parse(File.ReadAllText(_markerPath));

            if (marker.RootElement.TryGetProperty("plugins", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                return [.. list.EnumerateArray().Select(item => item.GetString() ?? "").Where(id => id.Length > 0)];
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // 读不出来就当没有
        }

        return [];
    }

    private sealed class Scope(CrashGuard guard, string? previous) : IDisposable
    {
        public void Dispose() => guard._activePlugin = previous;
    }
}
