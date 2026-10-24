using System.Reflection;
using ML.Plugin.Abstractions;

namespace ML.Plugin.Runtime;

/// <summary>给界面看的插件状态。</summary>
/// <summary>一个已经读进内存的界面片段。宿主把它交给页面去挂。</summary>
public sealed record PluginUiView(
    string PluginId,
    string Slot,
    string Html,
    string? Script,
    string? Title = null,
    string? IconPath = null,
    IReadOnlyList<string>? Replaces = null);

public sealed record PluginInfo
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Version { get; init; }

    /// <summary>清单里的 icon（插件目录内相对路径），没给就是 null。</summary>
    public string? Icon { get; init; }

    public string? Type { get; init; }

    public required string Directory { get; init; }

    /// <summary>
    /// <c>enabled</c> 已加载 · <c>disabled</c> 用户停用 · <c>error</c> 加载失败 ·
    /// <c>invalid</c> 清单有问题 · <c>rejected</c> 依赖/冲突被拒。
    /// </summary>
    public required string State { get; init; }

    public bool Enabled { get; init; }

    public string? Error { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public IReadOnlyList<string> Permissions { get; init; } = [];

    /// <summary>声明了、但用户还没同意的权限。非空时插件不会被加载（状态 needs-consent）。</summary>
    public IReadOnlyList<string> MissingPermissions { get; init; } = [];
}

/// <summary>
/// 插件系统门面：扫盘 → 排序 → 加载 → 开关 → 启动前钩子。
/// </summary>
/// <remarks>
/// 失败一律隔离到单个插件：初始化抛异常、超时、类型对不上，都只让这个插件变成
/// <c>error</c>，不影响其它插件，也不影响启动器本身。
/// </remarks>
public sealed class PluginManager : IDisposable
{
    private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(10);

    private readonly string _root;
    private readonly string _hostVersion;
    private readonly PluginStateStore _state;
    private readonly PluginAuditLog _audit;
    private readonly IHostServices _host;
    private readonly Action<string, string>? _log;
    private readonly object _gate = new();

    private List<PluginInfo> _infos = [];
    private List<LoadedPlugin> _loaded = [];

    /// <summary>上次加载时各插件的入口文件指纹（时间 + 长度）。用来发现"dll 被换过了"。</summary>
    private readonly Dictionary<string, string> _fingerprints = new(StringComparer.OrdinalIgnoreCase);
    private List<PluginUiView> _ui = [];

    public PluginManager(
        string pluginsRoot,
        string hostVersion,
        IHostServices host,
        string? auditLogPath = null,
        Action<string, string>? log = null)
    {
        _host = host;
        _root = pluginsRoot;
        _hostVersion = hostVersion;
        _log = log;
        _state = new PluginStateStore(Path.Combine(pluginsRoot, "state.json"));
        _audit = new PluginAuditLog(auditLogPath);
    }

    public string Root => _root;

    /// <summary>插件自己的可写数据目录。升级、换版本都保留，插件配置该放这里。</summary>
    public string DataRoot => Path.Combine(Path.GetDirectoryName(_root) ?? _root, "plugin-data");

    /// <summary>取某个插件的设置（界面和插件代码共用同一份文件）。</summary>
    public PluginSettingsStore SettingsFor(string id) =>
        new(Path.Combine(DataDirectoryFor(id), "settings.json"));

    private string DataDirectoryFor(string id)
    {
        string directory = Path.Combine(DataRoot, id);
        Directory.CreateDirectory(directory);
        return directory;
    }

    public IReadOnlyList<PluginInfo> Plugins
    {
        get
        {
            lock (_gate)
            {
                return _infos;
            }
        }
    }

    /// <summary>当前已加载插件提供的界面片段。页面拿到后自己挂到对应插槽。</summary>
    public IReadOnlyList<PluginUiView> UiContributions
    {
        get
        {
            lock (_gate)
            {
                return _ui;
            }
        }
    }

    /// <summary>重新扫盘并加载。已经在跑的插件先卸载。</summary>
    public IReadOnlyList<PluginInfo> Refresh()
    {
        lock (_gate)
        {
            var changed = new List<string>();

            UnloadAll();

            Directory.CreateDirectory(_root);

            IReadOnlyList<DiscoveredPlugin> discovered = PluginCatalog.Scan(_root);
            var infos = new List<PluginInfo>();
            var candidates = new List<PluginManifest>();

            foreach (DiscoveredPlugin item in discovered)
            {
                if (item.Manifest is null)
                {
                    infos.Add(new PluginInfo
                    {
                        Id = item.FolderName,
                        Name = item.FolderName,
                        Directory = item.Directory,
                        State = "invalid",
                        Enabled = false,
                        Error = item.Errors.Count > 0 ? item.Errors[0] : "清单有问题"
                    });
                    continue;
                }

                PluginManifest manifest = item.Manifest;

                if (!MeetsHostVersion(manifest, out string versionError))
                {
                    infos.Add(Describe(manifest, item.Directory, "rejected", false, versionError, item.Warnings));
                    continue;
                }

                if (!_state.IsEnabled(manifest.Id))
                {
                    infos.Add(Describe(manifest, item.Directory, "disabled", false, null, item.Warnings));
                    continue;
                }

                // 声明了但没同意的权限：不加载，等用户在界面上点"允许"。
                // 这里拦的是宿主真的会执行的权限（现在只有启动参数两条）。
                List<string> missing = MissingPermissions(manifest);
                if (missing.Count > 0)
                {
                    infos.Add(Describe(manifest, item.Directory, "needs-consent", true, null, item.Warnings) with
                    {
                        MissingPermissions = missing
                    });

                    _audit.Write(manifest.Id, "permissions", "consent-required", string.Join(" ", missing));
                    continue;
                }

                candidates.Add(manifest);
                infos.Add(Describe(manifest, item.Directory, "pending", true, null, item.Warnings));
            }

            PluginLoadPlan plan = PluginResolver.Resolve(candidates);

            foreach ((string id, string reason) in plan.Rejected)
            {
                int index = infos.FindIndex(p => p.Id == id);
                if (index >= 0)
                {
                    PluginInfo existing = infos[index];
                    infos[index] = existing with { State = "rejected", Enabled = false, Error = reason };
                }
            }

            foreach (PluginManifest manifest in plan.Order)
            {
                int index = infos.FindIndex(p => p.Id == manifest.Id);
                if (index < 0)
                {
                    continue;
                }

                (LoadedPlugin? loaded, string? error) = Load(manifest, infos[index].Directory);
                infos[index] = error is null
                    ? infos[index] with { State = "enabled" }
                    : infos[index] with { State = "error", Error = error };

                if (loaded is not null)
                {
                    _loaded.Add(loaded);

                    // 文件指纹和上次不同 = 这个插件的 dll 被重新编译/替换过
                    string fingerprint = Fingerprint(manifest, infos[index].Directory);

                    if (_fingerprints.TryGetValue(manifest.Id, out string? previous) && previous != fingerprint)
                    {
                        changed.Add(manifest.Id);
                    }

                    _fingerprints[manifest.Id] = fingerprint;
                }
            }

            _infos = infos;
            LastChanged = changed;

            return _infos;
        }
    }

    /// <summary>
    /// 最近一次刷新里，文件被换掉过的插件。
    /// </summary>
    /// <remarks>
    /// 热加载能把新 dll 装进来，但旧实例的静态状态、后台线程不一定收干净——
    /// 所以"插件文件变了"这件事值得提醒用户重启一次。要不要提，由界面上的设置决定。
    /// </remarks>
    public IReadOnlyList<string> LastChanged { get; private set; } = [];

    /// <summary>入口文件的时间 + 长度。没有 entry 的声明式插件退回用 manifest.json。</summary>
    private static string Fingerprint(PluginManifest manifest, string directory)
    {
        string target = manifest.Entry is { Length: > 0 } entry
            ? Path.Combine(directory, entry)
            : Path.Combine(directory, "manifest.json");

        try
        {
            var file = new FileInfo(target);

            return file.Exists
                ? $"{file.LastWriteTimeUtc.Ticks}/{file.Length}"
                : "missing";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "unknown";
        }
    }

    /// <summary>用户同意一组权限。同意后立刻重扫，插件会被真正加载。</summary>
    public IReadOnlyList<PluginInfo> Grant(string id, IEnumerable<string> permissions)
    {
        List<string> list = permissions.Where(PluginPermissions.IsEnforced).ToList();

        _state.Grant(id, list);
        _audit.Write(id, "permissions", "granted", string.Join(" ", list));

        return Refresh();
    }

    private List<string> MissingPermissions(PluginManifest manifest)
    {
        IReadOnlyList<string> required = PluginPermissions.Required(manifest.Permissions);
        if (required.Count == 0)
        {
            return [];
        }

        IReadOnlySet<string> granted = _state.Grants(manifest.Id);
        return required.Where(p => !granted.Contains(p)).ToList();
    }

    /// <summary>开/关一个插件。会立刻重扫，返回最新状态。</summary>
    public IReadOnlyList<PluginInfo> SetEnabled(string id, bool enabled)
    {
        _state.Set(id, enabled);
        return Refresh();
    }

    /// <summary>
    /// 跑启动前钩子。声明式插件直接贡献参数，程序集插件调自己的实现。
    /// 任何一个插件抛异常都只记录、跳过，不能让启动失败。
    /// </summary>
    /// <summary>把页面上的调用转给插件自己的代码。</summary>
    public async Task<string?> ExecuteCommandAsync(string id, string command, string? payload, CancellationToken cancellationToken)
    {
        LoadedPlugin? plugin;

        lock (_gate)
        {
            plugin = _loaded.FirstOrDefault(p => string.Equals(p.Manifest.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        if (plugin?.Command is null)
        {
            throw new InvalidOperationException($"插件 {id} 没有实现 IPluginCommand，或者没在运行。");
        }

        return await plugin.Command.ExecuteAsync(command, payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>广播宿主事件。任何一个插件抛异常都不能影响别人。</summary>
    public void RaiseEvent(string name, string? payloadJson)
    {
        List<LoadedPlugin> snapshot;

        lock (_gate)
        {
            snapshot = [.. _loaded];
        }

        foreach (LoadedPlugin plugin in snapshot)
        {
            if (plugin.Events is null)
            {
                continue;
            }

            try
            {
                plugin.Events.OnHostEvent(name, payloadJson);
            }
            catch (Exception ex)
            {
                _log?.Invoke("warn", $"插件 {plugin.Manifest.Id} 处理事件 {name} 时抛异常：{ex.Message}");
                _audit.Write(plugin.Manifest.Id, $"event:{name}", "error", ex.GetType().Name);
            }
        }
    }

    /// <summary>
    /// 跑启动钩子并合并参数。返回 null 表示放行，非 null 是要显示给用户的拒绝原因。
    /// </summary>
    public string? ApplyLaunchHooks(LaunchRequest request)
    {
        lock (_gate)
        {
            foreach (LoadedPlugin plugin in _loaded)
            {
                PluginManifest manifest = plugin.Manifest;
                bool mayJvm = HasPermission(manifest, PluginPermissions.LaunchJvmArgs);
                bool mayGame = HasPermission(manifest, PluginPermissions.LaunchGameArgs);

                // 在副本上跑：插件只能改副本，能不能落到真正的请求上由权限决定。
                // 这样"没权限就加不进去"是结构性保证，不靠插件自觉。
                var scratch = new LaunchRequest(
                    request.InstanceName,
                    request.VersionId,
                    request.PlayerName,
                    request.GameDirectory,
                    request.JvmArguments,
                    request.GameArguments);

                if (plugin.LaunchHook is not null)
                {
                    try
                    {
                        plugin.LaunchHook.OnBeforeLaunch(scratch);
                    }
                    catch (Exception ex)
                    {
                        _log?.Invoke("warn", $"插件 {manifest.Id} 的启动钩子抛异常，已跳过：{ex.Message}");
                        _audit.Write(manifest.Id, "launch-hook", "error", ex.GetType().Name);
                    }
                }

                // Tier1 的固定参数走同一条合并逻辑，不搞两套。
                foreach (string argument in manifest.JvmArguments)
                {
                    scratch.AddJvmArgument(argument);
                }

                foreach (string argument in manifest.GameArguments)
                {
                    scratch.AddGameArgument(argument);
                }

                List<string> addedJvm = scratch.JvmArguments.Skip(request.JvmArguments.Count).ToList();
                List<string> addedGame = scratch.GameArguments.Skip(request.GameArguments.Count).ToList();

                Merge(request, manifest.Id, addedJvm, mayJvm, PluginPermissions.LaunchJvmArgs, "JVM", request.AddJvmArgument);
                Merge(request, manifest.Id, addedGame, mayGame, PluginPermissions.LaunchGameArgs, "游戏", request.AddGameArgument);
            }

            // 参数都装配完了，再问"要不要放行"。
            // 拦在这儿而不是更早：守卫看到的是最终形态的启动请求。
            foreach (LoadedPlugin plugin in _loaded)
            {
                if (plugin.LaunchGuard is null)
                {
                    continue;
                }

                try
                {
                    HookResult? result = plugin.LaunchGuard.OnGuardLaunch(request);

                    if (result is { Allow: false })
                    {
                        string reason = string.IsNullOrWhiteSpace(result.Reason) ? "有插件拒绝了这次启动。" : result.Reason;

                        _audit.Write(plugin.Manifest.Id, "launch-guard", "denied", reason);
                        return reason;
                    }
                }
                catch (Exception ex)
                {
                    // 守卫自己炸了：当作放行，只记日志——不能因为它坏了让用户完全启动不了
                    _log?.Invoke("warn", $"插件 {plugin.Manifest.Id} 的启动守卫抛异常，已放行：{ex.Message}");
                    _audit.Write(plugin.Manifest.Id, "launch-guard", "error", ex.GetType().Name);
                }
            }

            return null;
        }
    }

    /// <summary>有权限就合并进真正的请求，没有就丢掉并记一条"被拒"。</summary>
    private void Merge(
        LaunchRequest request,
        string pluginId,
        List<string> added,
        bool allowed,
        string permission,
        string label,
        Action<string?> add)
    {
        if (added.Count == 0)
        {
            return;
        }

        if (!allowed)
        {
            _audit.Write(pluginId, permission, "denied", string.Join(" ", added));
            _log?.Invoke("warn", $"插件 {pluginId} 没有 {permission} 权限，加的 {label} 参数被丢弃：{string.Join(" ", added)}");
            return;
        }

        foreach (string argument in added)
        {
            add(argument);
        }

        _audit.Write(pluginId, permission, "allowed", string.Join(" ", added));
    }

    private bool HasPermission(PluginManifest manifest, string permission) =>
        manifest.Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase)
        && _state.Grants(manifest.Id).Contains(permission);

    public void Dispose()
    {
        lock (_gate)
        {
            UnloadAll();
        }
    }

    private (LoadedPlugin? Loaded, string? Error) Load(PluginManifest manifest, string directory)
    {
        if (manifest.IsDeclarative)
        {
            // 只有清单，没有代码：参数在 ApplyLaunchHooks 里加，界面在这里收。
            CollectUi(manifest, directory);
            _log?.Invoke("info", $"插件 {manifest.Id} 是声明式，已生效（无程序集）");
            return (new LoadedPlugin { Manifest = manifest }, null);
        }

        string entryPath = Path.Combine(directory, manifest.Entry!);

        if (!File.Exists(entryPath))
        {
            return (null, $"找不到入口文件 {manifest.Entry}");
        }

        PluginLoadContext? context = null;
        try
        {
            context = new PluginLoadContext(entryPath);
            Assembly assembly = context.LoadFromAssemblyPath(entryPath);
            Type? type = assembly.GetType(manifest.EntryType!, throwOnError: false);

            if (type is null)
            {
                context.Unload();
                return (null, $"程序集里找不到类型 {manifest.EntryType}");
            }

            if (Activator.CreateInstance(type) is not IPlugin instance)
            {
                context.Unload();
                return (null, $"{manifest.EntryType} 没有实现 IPlugin（或缺少无参构造函数）");
            }

            string dataDirectory = DataDirectoryFor(manifest.Id);
            var pluginContext = new HostPluginContext(
                manifest.Id,
                directory,
                dataDirectory,
                SettingsFor(manifest.Id),
                _host,
                _log);

            // 初始化可能挂住：给 10 秒，超时以后台继续跑，但这次当作失败。
            Task initialize = instance.InitializeAsync(pluginContext, CancellationToken.None);
            if (!initialize.Wait(InitializeTimeout))
            {
                context.Unload();
                return (null, $"初始化超过 {InitializeTimeout.TotalSeconds:0} 秒，已放弃加载");
            }

            initialize.GetAwaiter().GetResult();

            CollectUi(manifest, directory);
            _log?.Invoke("info", $"插件 {manifest.Id} {manifest.Version} 已加载");

            return (new LoadedPlugin
            {
                Manifest = manifest,
                Context = context,
                Instance = instance,
                LaunchHook = instance as ILaunchHook,
                LaunchGuard = instance as ILaunchGuard,
                Command = instance as IPluginCommand,
                Events = instance as IEventHook
            }, null);
        }
        catch (Exception ex)
        {
            // AggregateException 里才是插件真正抛的东西。
            Exception actual = ex is AggregateException aggregate && aggregate.InnerException is not null
                ? aggregate.InnerException
                : ex;

            context?.Unload();
            return (null, $"加载失败：{actual.GetType().Name}: {actual.Message}");
        }
    }

    private void UnloadAll()
    {
        foreach (LoadedPlugin plugin in _loaded)
        {
            try
            {
                plugin.Instance?.ShutdownAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                _log?.Invoke("warn", $"插件 {plugin.Manifest.Id} 卸载时抛异常：{ex.Message}");
            }

            plugin.Context?.Unload();
        }

        _loaded = [];
        _ui = [];

        // Unload() 只是"请求卸载"：程序集要等 GC 且所有引用都放开才真的走。
        // 不主动催一次的话，旧插件的静态字段、静态缓存会一直挂着——
        // 用户看到的是"停用了，但内存没降"，而且重新启用时可能摸到上一次的残留状态。
        // 两轮是常规做法：第一轮回收对象，finalizer 里可能又挂上引用，第二轮收尾。
        for (int round = 0; round < 2; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    /// <summary>
    /// 把清单里声明的界面片段读进内存。文件缺失只警告不报错——
    /// 一个插槽没挂上，不该让整个插件加载失败。
    /// </summary>
    private void CollectUi(PluginManifest manifest, string directory)
    {
        if (manifest.Ui.Count == 0)
        {
            return;
        }

        if (!HasPermission(manifest, PluginPermissions.UiPage))
        {
            _audit.Write(manifest.Id, PluginPermissions.UiPage, "denied", "界面片段未挂载");
            return;
        }

        foreach (PluginUiContribution contribution in manifest.Ui)
        {
            string htmlPath = Path.Combine(directory, contribution.HtmlPath);

            if (!File.Exists(htmlPath))
            {
                _log?.Invoke("warn", $"插件 {manifest.Id} 的界面文件不存在：{contribution.HtmlPath}");
                continue;
            }

            string? script = null;

            if (contribution.ScriptPath is not null)
            {
                string scriptPath = Path.Combine(directory, contribution.ScriptPath);

                if (File.Exists(scriptPath))
                {
                    script = File.ReadAllText(scriptPath);
                }
                else
                {
                    _log?.Invoke("warn", $"插件 {manifest.Id} 的界面脚本不存在：{contribution.ScriptPath}");
                }
            }

            _ui.Add(new PluginUiView(
                manifest.Id,
                contribution.Slot,
                File.ReadAllText(htmlPath),
                script,
                contribution.Title,
                contribution.IconPath,
                contribution.Replaces));
            _audit.Write(manifest.Id, PluginPermissions.UiPage, "allowed", contribution.Slot);
        }
    }

    private bool MeetsHostVersion(PluginManifest manifest, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(manifest.MinHostVersion))
        {
            return true;
        }

        if (!TryParseTriple(manifest.MinHostVersion, out Version required)
            || !TryParseTriple(_hostVersion, out Version host))
        {
            // 版本号写错不该拦住插件：当成"没声明"处理，但留一行日志。
            _log?.Invoke("warn", $"插件 {manifest.Id} 的 minHostVersion 格式不对（{manifest.MinHostVersion}），已忽略");
            return true;
        }

        if (host < required)
        {
            error = $"需要启动器 {manifest.MinHostVersion} 或更高，当前 {_hostVersion}";
            return false;
        }

        return true;
    }

    private static bool TryParseTriple(string text, out Version version)
    {
        version = new Version(0, 0);

        if (!PluginManifestParser.TryParseVersion(text, out int major, out int minor))
        {
            return false;
        }

        int patch = 0;
        string[] parts = text.Split('.');
        if (parts.Length > 2)
        {
            _ = int.TryParse(parts[2], out patch);
        }

        version = new Version(major, minor, patch);
        return true;
    }

    private static PluginInfo Describe(
        PluginManifest manifest,
        string directory,
        string state,
        bool enabled,
        string? error,
        IReadOnlyList<string> warnings) => new()
        {
            Id = manifest.Id,
            Name = manifest.Name,
            Version = manifest.Version,
            Type = manifest.Type,
            Icon = manifest.Icon,
            Directory = directory,
            State = state,
            Enabled = enabled,
            Error = error,
            Warnings = warnings,
            Permissions = manifest.Permissions
        };
}

/// <summary>交给插件的上下文实现。日志统一打上插件前缀，出问题能一眼找到源头。</summary>
internal sealed class HostPluginContext : IPluginContext
{
    private readonly Action<string, string>? _log;

    public HostPluginContext(
        string id,
        string directory,
        string dataDirectory,
        PluginSettingsStore settings,
        IHostServices host,
        Action<string, string>? log)
    {
        Host = host;
        Id = id;
        Directory = directory;
        DataDirectory = dataDirectory;
        Settings = settings;
        _log = log;
        Log = new HostPluginLogger(id, log);
    }

    public string Id { get; }

    public string Directory { get; }

    public string DataDirectory { get; }

    public IPluginSettings Settings { get; }

    public IHostServices Host { get; }

    public IPluginLogger Log { get; }
}

internal sealed class HostPluginLogger : IPluginLogger
{
    private readonly string _id;
    private readonly Action<string, string>? _log;

    public HostPluginLogger(string id, Action<string, string>? log)
    {
        _id = id;
        _log = log;
    }

    public void Info(string message) => _log?.Invoke("info", $"[{_id}] {message}");

    public void Warn(string message) => _log?.Invoke("warn", $"[{_id}] {message}");

    public void Error(string message, Exception? exception = null) =>
        _log?.Invoke("error", $"[{_id}] {message}{(exception is null ? string.Empty : $"：{exception.Message}")}");
}
