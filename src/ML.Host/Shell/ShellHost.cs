using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Windows.Forms;
using ML.Core.Configuration;
using System.Net.Http;
using ML.Features.Install;
using ML.Features.Mojang;
using ML.Host.Diagnostics;
using ML.Features.Download;
using ML.Core.Install;
using ML.Core.Instances;
using ML.Host.Accounts;
using ML.Host.Updates;
using ML.Plugin.Abstractions;
using ML.Plugin.Runtime;
using ML.Core.Java;
using ML.Core.Launch;
using ML.Core.Rules;
using ML.Core.Versioning;

namespace ML.Host.Shell;

/// <summary>
/// 界面之下的全部编排。它只认 JSON 进、JSON 出，不认任何 UI 框架——
/// 换成 WPF、换成一门 CLI 都只替换调用方。
/// </summary>
/// <remarks>
/// 两条约定：
/// ① 请求 → 立即回执，长任务在后台跑并推事件。<c>HandleAsync</c> 阻塞等待装机 3 分钟，
///    界面会像死了一样，这类操作必须立刻回执。
/// ② 事件统一走 <c>_emit</c>，格式 <c>{"event":"名字","data":{...}}</c>。
///    「进度」不是一个接口，「安装进度」「日志行」「结束」是三种事件，别混成一个。
/// </remarks>
internal sealed class ShellHost : IAsyncDisposable
{
    /// <summary>
    /// 全部进出 JSON 共用的一份选项。刻意关掉中文转义：默认编码器会把「安装功能不属于内核」
    /// 写成 <c>\u5B89\u88C5...</c>，JS 解析回来当然正确，但日志里人肉排查时一行全是转义码，
    /// 等于把可读性白扔了。这个流只走 <c>PostWebMessageAsJson</c>（按 JSON 解析，不按 HTML 解析），
    /// 前端又一律用 <c>textContent</c> 落地，所以不转义不会变成注入面。
    /// </summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private GamePaths _paths;
    private readonly InstanceStore _instances;
    private readonly SettingsStore _settings;
    private readonly MicrosoftAccountService _accounts;
    private readonly Action<string> _emit;

    /// <summary>
    /// 启动闸门：只圈"装配 → 进程 spawn"这一段，spawn 成功或失败就放行下一个。
    /// 不圈整个运行期——那等于又变回单开。
    /// </summary>
    private readonly SemaphoreSlim _startGate = new(1, 1);

    /// <summary>正在跑的实例 → 取消句柄。键是实例名，一个实例只能有一个进程。</summary>
    private readonly Dictionary<string, CancellationTokenSource> _running = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _runningGate = new();
    private CancellationTokenSource? _login;

    /// <summary>正在等用户授权的设备码。给「重新打开网页 / 复制代码」两个按钮用。</summary>
    private DeviceCodeInfo? _pendingCode;

    // ── 给 HostServices（插件看到的世界）用的入口 ──────────────
    // 都是 internal：插件拿到的是映射后的纯数据，不是这些内核类型本身。

    /// <summary>启动器主窗客户区尺寸（「窗口大小 = 与启动器一致」时用），由窗口层填。</summary>
    internal (int Width, int Height)? LauncherWindowSize { get; set; }

    /// <summary>屏幕工作区尺寸（「窗口大小 = 最大化」时用），由窗口层填。</summary>
    internal (int Width, int Height)? ScreenWorkArea { get; set; }

    internal GamePaths Paths => _paths;

    /// <summary>把一个子进程托管进来（绑 Job）。</summary>
    internal bool AdoptProcess(int processId) => _processGuard.Adopt(processId);

    /// <summary>建议的服务器目录。只是建议，插件想放别处也行。</summary>
    internal string ServerRootPath
    {
        get
        {
            string root = Path.Combine(DataRoot, "servers");
            Directory.CreateDirectory(root);

            return root;
        }
    }

    internal InstanceStore InstanceStore => _instances;

    /// <summary>这个实例现在是不是在跑（插件看到的世界里要用）。</summary>
    internal bool IsRunning(string instanceName)
    {
        lock (_runningGate)
        {
            return _running.ContainsKey(instanceName);
        }
    }

    /// <summary>本机已装好的版本（插件用的精简视图）。</summary>
    internal IReadOnlyList<(string Id, string? Type, int? Protocol)> InstalledVersions()
    {
        var versions = new List<(string Id, string? Type, int? Protocol)>();

        if (!Directory.Exists(_paths.Versions))
        {
            return versions;
        }

        foreach (string directory in Directory.EnumerateDirectories(_paths.Versions))
        {
            string id = Path.GetFileName(directory);
            string jsonPath = _paths.VersionJson(id);

            if (!File.Exists(jsonPath))
            {
                continue;
            }

            string? type = null;
            int? protocol = null;

            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(jsonPath));

                // 同一个文件顺手把协议号读出来：联机要对协议版本，多读一次文件不值当
                if (document.RootElement.TryGetProperty("type", out JsonElement element)
                    && element.ValueKind == JsonValueKind.String)
                {
                    type = element.GetString();
                }

                if (document.RootElement.TryGetProperty("protocolVersion", out JsonElement protocolElement)
                    && protocolElement.ValueKind == JsonValueKind.Number)
                {
                    protocol = protocolElement.GetInt32();
                }
            }
            catch (JsonException)
            {
                // 坏掉的 version.json 仍然算"装过"，只是没有类型信息。
            }

            versions.Add((id, type, protocol));
        }

        return versions;
    }

    /// <summary>账号簿里"最近使用"的那个；一个都没有返回 null。</summary>
    internal AccountView? CurrentAccountView()
    {
        var accounts = _accounts.List();

        if (accounts.Count == 0)
        {
            return null;
        }

        string? lastId = _accounts.LastUsedId;
        return accounts.FirstOrDefault(a => a.Id == lastId) ?? accounts[0];
    }

    /// <summary>插件发起的一次启动。失败只回原因，不抛给插件。</summary>
    internal async Task<HostLaunchResult> LaunchForPluginAsync(string instanceName, CancellationToken cancellationToken)
    {
        lock (_runningGate)
        {
            if (_running.ContainsKey(instanceName))
            {
                return new HostLaunchResult(false, $"实例「{instanceName}」已经在运行。");
            }
        }

        _ = Task.Run(() => LaunchAsync(instanceName), cancellationToken);

        await Task.CompletedTask.ConfigureAwait(false);
        return new HostLaunchResult(true, null);
    }

    /// <summary>插件系统。插件日志和游戏日志走同一条通道，出问题能对着时间线看。</summary>
    private readonly PluginManager _plugins;

    /// <summary>挑目录的入口，见构造函数注释。</summary>
    private readonly Func<string?>? _pickFolder;

    /// <summary>崩溃守护：记录"上次是不是崩的"、写出崩溃现场。</summary>
    private readonly CrashGuard _crash;

    /// <summary>重启自己的动作。窗口层才知道怎么起新进程，宿主只负责按下去。</summary>
    private readonly Action _restart;

    /// <summary>下载队列。通用服务：游戏安装、自定义链接、插件资源都走它。</summary>
    private readonly DownloadTaskQueue _downloads;

    /// <summary>子进程托管：绑上来的进程随启动器一起死（含崩溃）。</summary>
    private readonly ML.Host.Processes.ProcessGuard _processGuard;

    /// <summary>正在写的游戏日志，按实例名。多开时各写各的文件。</summary>
    private readonly ConcurrentDictionary<string, GameLogFile> _gameLogs = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="pickFolder">
    /// 让用户挑一个目录，取消返回 null。由宿主层用系统的文件夹选择框实现；
    /// ShellHost 自己不认识 WinForms，所以这里只收一个委托。
    /// </param>
    public ShellHost(
        string dataRoot,
        Action<string> emit,
        CrashGuard crash,
        Action restart,
        Func<string?>? pickFolder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(emit);
        ArgumentNullException.ThrowIfNull(crash);
        ArgumentNullException.ThrowIfNull(restart);

        _crash = crash;
        _restart = restart;
        _pickFolder = pickFolder;

        DataRoot = Path.GetFullPath(dataRoot);
        Directory.CreateDirectory(DataRoot);

        // 设置必须先于一切被读出来：游戏目录就在设置里，而它决定了 versions/ 往哪儿看。
        _settings = new SettingsStore(DataRoot);
        _settings.Load();

        _paths = new GamePaths(ResolveGameRoot());
        _instances = new InstanceStore(DataRoot);
        _emit = emit;

        _downloads = new DownloadTaskQueue(tasks => Emit("download.changed", new { tasks }));

        _processGuard = new ML.Host.Processes.ProcessGuard(text => Emit("log", new { level = "warn", instance = (string?)null, text }));
        ApplyDownloadSettings(_settings.Current);

        // 登录信息是宿主的事：内核不联网，拿到的只是启动参数里的四个字段。
        // 应用 ID 只认程序内置的那一个（作者要求）：玩家不能换、也不给他换的入口，
        // 这样所有人授权页上看到的都是同一个应用名。
        _accounts = new MicrosoftAccountService(
            DataRoot,
            () => AccountDefaults.MicrosoftClientId);

        _plugins = new PluginManager(
            Path.Combine(DataRoot, "plugins"),
            LauncherVersion,
            new Plugins.HostServices(this),
            Path.Combine(DataRoot, "logs", "plugin-audit.log"),
            (level, text) => Emit("log", new { level, instance = (string?)null, text }));

        // 启动时先装一遍：插件页看到的状态必须是"真的加载过"的结果，
        // 不能等用户点开那一页才去加载（那样第一屏的启动就已经错过钩子了）。
        _plugins.Refresh();

        // 写下"我在运行"。正常退出会删掉它；下次启动发现它还在，就说明这次没善终。
        // 必须放在 _plugins 初始化之后——这里要读已加载的插件列表。
        _crash.MarkRunning(_plugins.Plugins.Select(plugin => plugin.Id));
    }

    /// <summary>启动器版本。既写进启动参数，也用来校验插件的 minHostVersion。</summary>
    public const string LauncherVersion = "0.1.0";

    public string DataRoot { get; }

    public LauncherSettings Settings => _settings.Current;

    /// <summary>
    /// 当前生效的游戏目录。没指定时回落到数据目录——这样下游代码不必到处判空，
    /// 而「没指定」这件事由 <see cref="LauncherSettings.HasGameRoot"/> 在入口处说明白。
    /// </summary>
    private string ResolveGameRoot() => _settings.Current.ActiveGameRoot?.Path ?? DataRoot;

    public async Task<string> HandleAsync(string requestJson, CancellationToken cancellationToken = default)
    {
        long seq = 0;

        try
        {
            using var doc = JsonDocument.Parse(requestJson);
            var root = doc.RootElement;

            seq = root.TryGetProperty("seq", out var s) ? s.GetInt64() : 0;
            string cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";
            var payload = root.TryGetProperty("data", out var d) ? d : default;

            object result = cmd switch
            {
                "app.info" => AppInfo(),
                "settings.get" => SettingsGet(),
                "settings.update" => SettingsUpdate(payload),
                "settings.detect" => SettingsDetect(),
                "memory.info" => MemoryInfo(),
                "version.list" => await VersionListAsync(cancellationToken).ConfigureAwait(false),
                "instance.list" => InstanceList(),
                "instance.create" => CreateInstance(payload),
                "instance.update" => UpdateInstance(payload),
                "instance.delete" => DeleteInstance(payload),
                "install.start" => await StartInstallAsync(payload, cancellationToken).ConfigureAwait(false),
                "launch.start" => await StartLaunchAsync(payload, cancellationToken).ConfigureAwait(false),
                "launch.kill" => KillLaunch(payload),
                "java.list" => JavaList(),
                "account.list" => AccountList(),
                "account.add.offline" => AccountAddOffline(payload),
                "account.remove" => AccountRemove(payload),
                "account.remember" => AccountRemember(payload),
                "account.login.start" => await AccountLoginStartAsync().ConfigureAwait(false),
                "account.login.cancel" => AccountLoginCancel(),
                "account.login.open" => AccountLoginOpen(),
                "account.login.copy" => AccountLoginCopy(),
                "shell.open" => ShellOpen(payload),
                "plugin.list" => PluginList(),
                "plugin.toggle" => PluginToggle(payload),
                "plugin.reload" => PluginReload(),
                "plugin.install" => await PluginInstallAsync(payload).ConfigureAwait(false),
                "plugin.grant" => PluginGrant(payload),
                "plugin.ui" => PluginUi(),
                "plugin.command" => await PluginCommandAsync(payload).ConfigureAwait(false),
                "plugin.settings.get" => PluginSettingsGet(payload),
                "plugin.settings.set" => PluginSettingsSet(payload),
                "shell.openFolder" => ShellOpenFolder(payload),
                "shell.pickFolder" => ShellPickFolder(),
                "shell.openUrl" => ShellOpenUrl(payload),
                "gameRoot.list" => GameRootState(),
                "gameRoot.add" => GameRootAdd(payload),
                "gameRoot.note" => GameRootNote(payload),
                "gameRoot.remove" => GameRootRemove(payload),
                "gameRoot.select" => GameRootSelect(payload),
                "instance.verify" => await VerifyInstanceAsync(payload, cancellationToken).ConfigureAwait(false),
                "log.info" => GameLogInfo(),
                "crash.info" => CrashInfo(),
                "crash.clear" => CrashClear(),
                "crash.export" => CrashExport(),
                "crash.restart" => CrashRestart(payload),
                "download.list" => DownloadList(),
                "download.add" => DownloadAdd(payload),
                "download.pause" => DownloadControl(payload, "pause"),
                "download.resume" => DownloadControl(payload, "resume"),
                "download.cancel" => DownloadControl(payload, "cancel"),
                "download.remove" => DownloadControl(payload, "remove"),
                "log.clear" => GameLogClear(),
                "update.check" => await UpdateCheckAsync(cancellationToken).ConfigureAwait(false),
                "update.apply" => await UpdateApplyAsync(cancellationToken).ConfigureAwait(false),
                _ => new { ok = false, error = $"未知命令：{cmd}" }
            };

            return JsonSerializer.Serialize(new { seq, payload = result }, Json);
        }
        catch (Exception ex)
        {
            EmitError(ex);
            return JsonSerializer.Serialize(
                new { seq, payload = new { ok = false, error = $"{ex.GetType().Name}: {ex.Message}" } },
                Json);
        }
    }

    private object AppInfo() => new
    {
        ok = true,
        dataRoot = DataRoot,
        gameRoot = _paths.Root,
        hasGameRoot = Settings.HasGameRoot,
        version = UpdateService.CurrentVersion,
        updateFeed = UpdateService.FeedDescription,
        updateConfigured = UpdateService.FeedUrl is not null,
        os = Environment.OSVersion.VersionString,
        settings = Settings,
        settingsFile = _settings.FilePath,
        accounts = _accounts.List(),
        lastAccountId = _accounts.LastUsedId
    };

    // ── 账号簿 ───────────────────────────────────────────────────────
    //
    // 正版与离线在同一个簿子里：界面上一排排并列，启动时按 id 取一个。
    // 全部走宿主：内核拿到的只是 LaunchAccount 的几个字段，它不知道有网络这回事。

    private object AccountList() => new
    {
        ok = true,
        accounts = _accounts.List(),
        lastAccountId = _accounts.LastUsedId,
        clientIdConfigured = _accounts.ClientId is not null,
        // 界面据此决定要不要提示「这个构建没配登录应用」。没有「用户自己填」这条路了。
        clientIdSource = string.IsNullOrWhiteSpace(AccountDefaults.MicrosoftClientId) ? "none" : "builtin",
        fallbackReason = _accounts.LastFallbackReason,
        loggingIn = _login is not null
    };

    private object AccountAddOffline(JsonElement payload)
    {
        string name = Required(payload, "name");

        try
        {
            AccountView created = _accounts.AddOffline(name);
            Emit("account.changed", new { accounts = _accounts.List(), lastAccountId = _accounts.LastUsedId });
            return new { ok = true, account = created, accounts = _accounts.List() };
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return new { ok = false, error = ex.Message };
        }
    }

    private object AccountRemove(JsonElement payload)
    {
        string id = Required(payload, "id");

        if (!_accounts.Remove(id))
        {
            return new { ok = false, error = "这个账号已经不在账号簿里了。" };
        }

        Emit("account.changed", new { accounts = _accounts.List(), lastAccountId = _accounts.LastUsedId });
        return new { ok = true, accounts = _accounts.List() };
    }

    private object AccountRemember(JsonElement payload)
    {
        string id = Required(payload, "id");
        _accounts.Remember(id);
        return new { ok = true, lastAccountId = _accounts.LastUsedId };
    }

    private async Task<object> AccountLoginStartAsync()
    {
        if (_login is not null)
        {
            return new { ok = false, error = "登录已经在进行中。先把上一条设备码用完，或者点取消。" };
        }

        if (_accounts.ClientId is null)
        {
            return new { ok = false, error = "还没填微软应用 ID。到 portal.azure.com 注册一个公共客户端应用，把「应用程序(客户端) ID」填进输入框。" };
        }

        DeviceCodeInfo info;

        try
        {
            info = await _accounts.BeginLoginAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return new { ok = false, error = ex.Message };
        }

        var cts = new CancellationTokenSource();
        _login = cts;
        _pendingCode = info;

        // 这两步是整套流程里最影响手感的地方：码直接进剪贴板、浏览器直接弹出来，
        // 用户只需要「粘贴 → 登录 → 接受」。失败不致命，界面会退回手动模式（手动点按钮）。
        bool copied = TryCopyToClipboard(info.UserCode);
        bool opened = TryOpenBrowser(info.VerificationUri);

        Emit("account.devicecode", new
        {
            userCode = info.UserCode,
            verificationUri = info.VerificationUri,
            message = info.Message,
            expiresIn = (int)info.ExpiresIn.TotalSeconds,
            copied,
            browserOpened = opened
        });

        _ = Task.Run(async () =>
        {
            try
            {
                AccountView account = await _accounts.CompleteLoginAsync(info, cts.Token).ConfigureAwait(false);
                _pendingCode = null;
                Emit("account.changed", new
                {
                    accounts = _accounts.List(),
                    lastAccountId = _accounts.LastUsedId,
                    added = account
                });
            }
            catch (OperationCanceledException)
            {
                _pendingCode = null;
                Emit("account.login.failed", new { error = "登录已取消。" });
            }
            catch (Exception ex)
            {
                _pendingCode = null;
                Emit("account.login.failed", new { error = ex.Message });
            }
            finally
            {
                if (ReferenceEquals(_login, cts))
                {
                    _login = null;
                }
            }
        });

        return new
        {
            ok = true,
            userCode = info.UserCode,
            verificationUri = info.VerificationUri,
            message = info.Message,
            expiresIn = (int)info.ExpiresIn.TotalSeconds,
            copied,
            browserOpened = opened
        };
    }

    private object AccountLoginCancel()
    {
        _login?.Cancel();
        _pendingCode = null;
        return new { ok = true };
    }

    /// <summary>重新打开登录网页（用户关早了，或者默认浏览器没接住）。</summary>
    private object AccountLoginOpen()
    {
        if (_pendingCode is null)
        {
            return new { ok = false, error = "现在没有等授权的登录。" };
        }

        return new { ok = TryOpenBrowser(_pendingCode.VerificationUri) };
    }

    /// <summary>再复制一次设备码。</summary>
    private object AccountLoginCopy()
    {
        if (_pendingCode is null)
        {
            return new { ok = false, error = "现在没有等授权的登录。" };
        }

        bool copied = TryCopyToClipboard(_pendingCode.UserCode);
        return new { ok = copied, error = copied ? null : "复制失败，手动抄一下上面的码。" };
    }

    private static bool TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // 没有默认浏览器、被策略挡住之类，交给界面提示手动打开。
            return false;
        }
    }

    private object PluginList() => new
    {
        ok = true,
        root = _plugins.Root,
        plugins = _plugins.Plugins.Select(ToPluginView).ToList()
    };

    private object PluginToggle(JsonElement payload)
    {
        string id = Required(payload, "id");
        bool enabled = payload.TryGetProperty("enabled", out JsonElement value) && value.ValueKind == JsonValueKind.True;

        var plugins = _plugins.SetEnabled(id, enabled);
        Emit("log", new
        {
            level = "info",
            instance = (string?)null,
            text = enabled ? $"插件 {id} 已启用" : $"插件 {id} 已停用"
        });

        return new { ok = true, root = _plugins.Root, plugins = plugins.Select(ToPluginView).ToList() };
    }

    /// <summary>
    /// 装插件：本地目录 / .mlp 压缩包 / GitHub 仓库。
    /// 装完立刻重扫，返回最新列表——界面不需要再手动刷新一次。
    /// </summary>
    private async Task<object> PluginInstallAsync(JsonElement payload)
    {
        string source = Required(payload, "source").Trim();
        string kind = payload.TryGetProperty("kind", out JsonElement kindElement)
                      && kindElement.ValueKind == JsonValueKind.String
            ? kindElement.GetString() ?? "folder"
            : "folder";

        InstallResult result = kind == "github"
            ? await PluginInstaller.InstallFromGitHubAsync(source, _plugins.Root, CancellationToken.None).ConfigureAwait(false)
            : PluginInstaller.InstallFromLocalPath(source, _plugins.Root);

        if (!result.Ok)
        {
            return new { ok = false, error = result.Message ?? "安装失败。" };
        }

        Emit("log", new { level = "info", instance = (string?)null, text = $"已安装插件 {result.PluginId}" });

        return new
        {
            ok = true,
            id = result.PluginId,
            root = _plugins.Root,
            plugins = _plugins.Refresh().Select(ToPluginView).ToList()
        };
    }

    /// <summary>在资源管理器里打开某个目录（现在的用途：打开插件目录）。</summary>
    private object ShellOpenFolder(JsonElement payload)
    {
        string path = payload.TryGetProperty("path", out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : _plugins.Root;

        if (!Directory.Exists(path))
        {
            return new { ok = false, error = $"目录不存在：{path}" };
        }

        try
        {
            // 不要显式去起 explorer.exe：那等于"一个程序在调用系统资源管理器"，
            // 360 这类防护会直接报"可疑操作"。把目录交给 shell 自己处理就正常了。
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return new { ok = true };
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new { ok = false, error = "打不开资源管理器。" };
        }
    }

    /// <summary>上次是不是崩的 + 历史崩溃文件。前端据此弹恢复对话框。</summary>
    private object CrashInfo()
    {
        CrashReport? pending = _crash.ReadPending();

        return new
        {
            ok = true,
            directory = _crash.Root,
            pending,
            history = _crash.ListCrashes()
                .Take(20)
                .Select(item => new { path = item.Path, time = item.Time, bytes = item.Bytes })
                .ToArray()
        };
    }

    /// <summary>手动清掉"上次崩过"的标记（用户点"我知道了"）。</summary>
    private object CrashClear()
    {
        _crash.ClearPending();

        return new { ok = true };
    }

    /// <summary>
    /// 导出崩溃现场成一份人看得懂的文本，落在崩溃目录里并打开它。
    /// 用"文件 + 打开目录"而不是保存对话框：崩溃时用户往往手忙脚乱，少一步选择更好。
    /// </summary>
    private object CrashExport()
    {
        CrashReport? pending = _crash.ReadPending();

        if (pending is null)
        {
            return new { ok = false, error = "没有待导出的崩溃记录。" };
        }

        string path = Path.Combine(_crash.Root, $"崩溃报告-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.txt");

        var lines = new List<string>
        {
            "Enderman Launcher 崩溃报告",
            new string('=', 46),
            $"时间    ：{pending.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}",
            $"版本    ：{pending.Version}",
            $"来源    ：{pending.Source}",
            $"涉事插件：{pending.ActivePlugin ?? "(没能定位到具体插件)"}",
            $"异常    ：{pending.ExceptionType}",
            $"消息    ：{pending.Message}",
            "",
            "已加载插件："
        };

        lines.AddRange(pending.LoadedPlugins.Select(id => $"  - {id}"));
        lines.AddRange(["", "堆栈：", pending.StackTrace, "", $"内存里最后 {pending.RecentEvents.Count} 条事件："]);
        lines.AddRange(pending.RecentEvents);

        try
        {
            File.WriteAllLines(path, lines, new System.Text.UTF8Encoding(true));

            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new { ok = false, error = ex.Message };
        }

        return new { ok = true, path };
    }

    /// <summary>
    /// 重启。带 <c>pluginId</c> 时先把它禁掉（"就是它干的"），带 <c>all=true</c> 时全禁（"我不查了，先能用"）。
    /// </summary>
    private object CrashRestart(JsonElement payload)
    {
        string? pluginId = payload.TryGetProperty("pluginId", out JsonElement id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;

        bool disableAll = payload.TryGetProperty("all", out JsonElement all) && all.ValueKind == JsonValueKind.True;

        var disabled = new List<string>();

        if (disableAll)
        {
            foreach (PluginInfo plugin in _plugins.Plugins.Where(plugin => plugin.Enabled))
            {
                _plugins.SetEnabled(plugin.Id, false);
                disabled.Add(plugin.Id);
            }
        }
        else if (!string.IsNullOrWhiteSpace(pluginId))
        {
            _plugins.SetEnabled(pluginId, false);
            disabled.Add(pluginId);
        }

        _crash.ClearPending();
        _restart();

        return new { ok = true, disabled };
    }

    /// <summary>把设置里的三个下载参数同步给下载器。</summary>
    private void ApplyDownloadSettings(LauncherSettings settings)
    {
        _downloads.ThreadsPerTask = settings.DownloadThreads;
        _downloads.MaxParallelTasks = settings.DownloadParallelTasks;
        _downloads.BytesPerSecond = settings.DownloadSpeedLimitMb <= 0
            ? 0
            : settings.DownloadSpeedLimitMb * 1024L * 1024L;
    }

    private object DownloadList() => new
    {
        ok = true,
        tasks = _downloads.Snapshot(),
        downloadRoot = Path.Combine(DataRoot, "downloads")
    };

    /// <summary>
    /// 新建自定义下载任务：<b>一行一个链接，每行各自一个任务</b>。
    /// </summary>
    /// <remarks>
    /// 不做合并：用户粘几行就是想下几个东西，合成一个任务反而看不出谁是谁。
    /// （游戏安装那边是另一回事——"版本 + 加载器"是同一件事，所以那边仍然是一个任务。）
    /// </remarks>
    private object DownloadAdd(JsonElement payload)
    {
        var urls = new List<string>();

        if (payload.TryGetProperty("urls", out JsonElement array) && array.ValueKind == JsonValueKind.Array)
        {
            urls.AddRange(array.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .Where(item => !string.IsNullOrWhiteSpace(item)));
        }
        else if (payload.TryGetProperty("url", out JsonElement single) && single.ValueKind == JsonValueKind.String)
        {
            urls.AddRange((single.GetString() ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }

        if (urls.Count == 0)
        {
            return new { ok = false, error = "没有可下载的地址。" };
        }

        string root = Path.Combine(DataRoot, "downloads");
        var errors = new List<string>();
        int created = 0;

        // 同一批里出现同名文件时把域名挂在标题上，否则任务列表里两条一模一样的名字没法区分
        var repeated = urls
            .Select(url => Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? parsed) ? SafeFileName(parsed) : "")
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in urls)
        {
            string url = raw.Trim();

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                errors.Add($"跳过 {url}：只支持 http/https 链接");
                continue;
            }

            string name = SafeFileName(uri);

            var item = new DownloadItem
            {
                Url = url,
                DestinationPath = UniquePath(root, name)
            };

            string title = repeated.Contains(name) ? $"{name}（{uri.Host}）" : name;

            if (_downloads.Enqueue(title, [item]) is null)
            {
                errors.Add($"队列已满，剩下的没加进去（最多 {DownloadTaskQueue.MaxQueuedTasks} 个任务）");
                break;
            }

            created++;
        }

        return new
        {
            ok = created > 0,
            created,
            errors,
            tasks = _downloads.Snapshot(),
            downloadRoot = root
        };
    }

    /// <summary>
    /// 解析 <c>ip</c> / <c>ip:port</c>。端口缺省 25565；解析不出端口就整串当主机名。
    /// </summary>
    private static (string Host, int Port) ParseServer(string raw)
    {
        string text = raw.Trim();
        int colon = text.LastIndexOf(':');

        // 只有最后一段是数字才当端口；IPv6 里冒号很多，那种情况整串当主机名
        if (colon > 0 && colon < text.Length - 1
            && int.TryParse(text[(colon + 1)..], out int port)
            && port is > 0 and <= 65535
            && !text.Contains('[', StringComparison.Ordinal))
        {
            return (text[..colon], port);
        }

        return (text, 25565);
    }

    private object DownloadControl(JsonElement payload, string action)
    {
        string id = Required(payload, "id");

        bool ok = action switch
        {
            "pause" => _downloads.Pause(id),
            "resume" => _downloads.Resume(id),
            "cancel" => _downloads.Cancel(id),
            "remove" => _downloads.Remove(id),
            _ => false
        };

        return ok
            ? new { ok = true, tasks = _downloads.Snapshot() }
            : new { ok = false, error = "这个任务现在不能这么做。" };
    }

    /// <summary>从 URL 猜文件名：去掉查询串和非法字符，猜不出来就用 download.bin。</summary>
    private static string SafeFileName(Uri uri)
    {
        string name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));

        if (string.IsNullOrWhiteSpace(name))
        {
            return "download.bin";
        }

        foreach (char bad in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(bad, '_');
        }

        name = name.Trim().TrimEnd('.');

        return name.Length == 0 ? "download.bin" : name;
    }

    /// <summary>同名文件不覆盖：加 -1、-2，避免把用户上一次下的东西冲掉。</summary>
    private static string UniquePath(string directory, string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        string candidate = Path.Combine(directory, fileName);

        for (int index = 1; File.Exists(candidate) || Directory.Exists(candidate + ".part"); index++)
        {
            candidate = Path.Combine(directory, $"{stem}-{index}{extension}");
        }

        return candidate;
    }

    /// <summary>
    /// 完整校验一个实例的文件（逐个算 SHA1）。慢，所以只由界面上手动触发。
    /// </summary>
    /// <remarks>
    /// 启动前那次检查只 stat + 比大小；这里才真的把文件读一遍。
    /// 结论里带上前若干个坏文件的绝对路径，用户能直接看到是哪个坏了。
    /// </remarks>
    private async Task<object> VerifyInstanceAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        string name = Required(payload, "name");
        Instance instance = _instances.Find(name)
            ?? throw new InvalidOperationException($"实例「{name}」不存在。");

        string versionPath = _paths.VersionJson(instance.Metadata.VersionId);

        if (!File.Exists(versionPath))
        {
            return new { ok = false, error = $"版本 {instance.Metadata.VersionId} 还没装。" };
        }

        VersionMetadata? version = JsonSerializer.Deserialize<VersionMetadata>(await File.ReadAllTextAsync(versionPath, cancellationToken).ConfigureAwait(false));

        if (version is null)
        {
            return new { ok = false, error = "version.json 读不出来。" };
        }

        var installer = new GameInstaller(_paths, new DownloadQueue(new FileDownloader(new HttpClient())));
        IReadOnlyList<DownloadItem> plan = installer.PlanDownloads(version, CurrentEnvironment());
        AuditReport audit = await FileAudit.FullCheckAsync(plan, progress: null, cancellationToken).ConfigureAwait(false);

        return new
        {
            ok = true,
            total = audit.Total,
            missing = audit.Missing,
            wrongSize = audit.WrongSize,
            wrongHash = audit.WrongHash,
            healthy = audit.Healthy,
            broken = audit.Broken.Take(20).Select(item => item.DestinationPath).ToArray()
        };
    }

    /// <summary>日志目录 + 文件清单。清理弹窗前用它显示"有多少要删"。</summary>
    private object GameLogInfo()
    {
        string directory = Path.Combine(DataRoot, "logs");

        long total = 0;
        int count = 0;

        if (Directory.Exists(directory))
        {
            foreach (FileInfo file in new DirectoryInfo(directory).GetFiles("*.log"))
            {
                count++;
                total += file.Length;
            }
        }

        return new
        {
            ok = true,
            directory,
            count,
            totalBytes = total,
            // 正在写的那个：清理时跳过，界面上也可以标一下
            active = _gameLogs.Values.Where(log => log.IsActive).Select(log => log.Path).ToArray()
        };
    }

    /// <summary>
    /// 启动前的快速检查与补齐。<see cref="LauncherSettings.CheckFilesBeforeLaunch"/> 关掉就整个跳过。
    /// </summary>
    private async Task VerifyFilesBeforeLaunchAsync(string name, VersionMetadata version)
    {
        if (Settings.CheckFilesBeforeLaunch == false)
        {
            return;
        }

        var installer = new GameInstaller(_paths, new DownloadQueue(new FileDownloader(new HttpClient())));
        IReadOnlyList<DownloadItem> plan = installer.PlanDownloads(version, CurrentEnvironment());
        AuditReport audit = FileAudit.QuickCheck(plan);

        if (audit.Healthy)
        {
            return;
        }

        Emit("log", new
        {
            level = "warn",
            instance = name,
            text = $"启动前检查：{plan.Count} 个文件里缺 {audit.Missing} 个、大小不符 {audit.WrongSize} 个"
        });

        if (Settings.AutoRepairMissing == false)
        {
            return;
        }

        string? taskId = _downloads.Enqueue($"补齐 · {name}", audit.Broken);

        if (taskId is null)
        {
            Emit("log", new { level = "warn", instance = name, text = "下载队列已满，这次不补，直接启动。" });
            return;
        }

        Emit("log", new
        {
            level = "info",
            instance = name,
            text = $"正在补齐 {audit.Broken.Count} 个文件（进度见下载页）…"
        });

        bool ok = await _downloads.WaitForAsync(taskId).ConfigureAwait(false);

        Emit("log", new
        {
            level = ok ? "info" : "warn",
            instance = name,
            text = ok ? "补齐完成，继续启动。" : "补齐没成功，仍然尝试启动（可能起不来）。"
        });
    }

    /// <summary>
    /// 清空日志目录，但跳过正在写的文件——那是当前这局游戏的日志，删了等于把现场毁了。
    /// </summary>
    private object GameLogClear()
    {
        string directory = Path.Combine(DataRoot, "logs");

        if (!Directory.Exists(directory))
        {
            return new { ok = true, deleted = 0, skipped = 0, failed = 0 };
        }

        var active = _gameLogs.Values
            .Where(log => log.IsActive)
            .Select(log => System.IO.Path.GetFullPath(log.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int deleted = 0;
        int skipped = 0;
        int failed = 0;

        foreach (FileInfo file in new DirectoryInfo(directory).GetFiles("*.log"))
        {
            if (active.Contains(file.FullName))
            {
                skipped++;
                continue;
            }

            try
            {
                file.Delete();
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 别的程序还捏着这个文件（比如用户开着记事本在看）——不算致命，数一下告诉界面
                failed++;
            }
        }

        Emit("log", new
        {
            level = "info",
            instance = (string?)null,
            text = $"清理启动器日志：删除 {deleted} 个，跳过正在写入 {skipped} 个" + (failed > 0 ? $"，{failed} 个删不掉" : "")
        });

        return new { ok = true, deleted, skipped, failed };
    }

    /// <summary>
    /// 诊断头信息。排查"起不来"时只有这段有答案：崩在 JVM 启动阶段时，
    /// 游戏自己的 latest.log 根本不会生成。
    /// </summary>
    private string BuildDiagnosticPreamble(string name, Instance instance, LaunchCommand command, int memoryMb)
    {
        var builder = new System.Text.StringBuilder();

        builder.AppendLine("================= 诊断信息 =================");
        builder.AppendLine($"时间    ：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"实例    ：{name}（版本 {instance.Metadata.VersionId}）");
        builder.AppendLine($"Java    ：{command.JavaExecutable}");
        builder.AppendLine($"工作目录：{command.WorkingDirectory}");
        builder.AppendLine($"内存    ：{memoryMb} MB（{(Settings.UseCustomMemory ? "自定义" : "自动配置")}），GC 预设 {Settings.GcPreset}");
        builder.AppendLine($"优先级  ：{Settings.GamePriority}，窗口模式 {Settings.GameWindowMode}");
        builder.AppendLine();
        builder.AppendLine("完整命令：");

        var line = new System.Text.StringBuilder("  ").Append(Quote(command.JavaExecutable));

        foreach (string argument in command.Arguments)
        {
            line.Append(' ').Append(Quote(argument));
        }

        builder.AppendLine(line.ToString());
        builder.AppendLine();
        builder.AppendLine($"classpath（{command.Classpath.Count} 条）：");

        foreach (string entry in command.Classpath)
        {
            builder.AppendLine($"  - {entry}");
        }

        if (command.MissingClasspathFiles.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"!! classpath 里有 {command.MissingClasspathFiles.Count} 个文件不存在：");

            foreach (string missing in command.MissingClasspathFiles)
            {
                builder.AppendLine($"  - {missing}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("环境变量：");

        foreach (string key in new[] { "JAVA_HOME", "PATH", "USERPROFILE", "TEMP" })
        {
            string? value = Environment.GetEnvironmentVariable(key);

            if (!string.IsNullOrEmpty(value))
            {
                builder.AppendLine($"  {key}={value}");
            }
        }

        builder.AppendLine("===========================================");

        return builder.ToString();
    }

    /// <summary>参数里有空格时日志里会看不出边界，加引号是为了"能照着抄回命令行"。</summary>
    private static string Quote(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;

    /// <summary>目录列表 + 当前选中。前端每次改完都用它整体刷新，避免自己拼状态。</summary>
    private object GameRootState()
    {
        LauncherSettings settings = Settings;

        return new
        {
            ok = true,
            gameRoots = settings.GameRoots.Select(root => new
            {
                id = root.Id,
                note = root.Note,
                path = root.Path,
                // 路径可能被删/被拔盘：如实告诉界面，不自动从列表里抹掉
                exists = Directory.Exists(root.Path)
            }).ToArray(),
            activeGameRootId = settings.ActiveGameRoot?.Id,
            gameRoot = _paths.Root,
            hasGameRoot = settings.HasGameRoot
        };
    }

    /// <summary>选一个目录加进列表并选中。已经在列表里的只切换，不重复加。</summary>
    private object GameRootAdd(JsonElement payload)
    {
        string path = Required(payload, "path").Trim();
        string full;

        try
        {
            full = GameRootEntry.NormalizePath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new { ok = false, error = $"这个路径不能用：{path}" };
        }

        if (!Directory.Exists(full))
        {
            return new { ok = false, error = $"目录不存在：{full}" };
        }

        GameRootEntry entry = GameRootEntry.FromPath(full);
        bool existed = Settings.GameRoots.Any(root => root.Id == entry.Id);

        _settings.Update(current => existed
            ? current with { ActiveGameRootId = entry.Id }
            : current with
            {
                GameRoots = [.. current.GameRoots, entry],
                ActiveGameRootId = entry.Id
            });

        _paths = new GamePaths(ResolveGameRoot());

        return new
        {
            ok = true,
            existed,
            // 目录里没有 versions：加是让加的，但要说清楚，免得建实例时一脸问号
            hasVersions = Directory.Exists(_paths.Versions),
            state = GameRootState()
        };
    }

    /// <summary>改备注。只改配置里的名字，不动磁盘文件夹。</summary>
    private object GameRootNote(JsonElement payload)
    {
        string id = Required(payload, "id");
        string note = payload.TryGetProperty("note", out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

        if (Settings.GameRoots.All(root => root.Id != id))
        {
            return new { ok = false, error = "这个目录不在列表里。" };
        }

        _settings.Update(current => current with
        {
            GameRoots = [.. current.GameRoots.Select(root => root.Id == id
                ? new GameRootEntry { Id = root.Id, Path = root.Path, Note = note.Trim() }.Normalize()
                : root)]
        });

        return new { ok = true, state = GameRootState() };
    }

    /// <summary>从列表里移除。只删记录，不碰磁盘；删掉的正好是选中项就顺延到第一条。</summary>
    private object GameRootRemove(JsonElement payload)
    {
        string id = Required(payload, "id");

        if (Settings.GameRoots.All(root => root.Id != id))
        {
            return new { ok = false, error = "这个目录不在列表里。" };
        }

        _settings.Update(current =>
        {
            List<GameRootEntry> rest = [.. current.GameRoots.Where(root => root.Id != id)];
            string? active = current.ActiveGameRootId == id
                ? rest.FirstOrDefault()?.Id
                : current.ActiveGameRootId;

            return current with { GameRoots = rest, ActiveGameRootId = active };
        });

        _paths = new GamePaths(ResolveGameRoot());

        return new { ok = true, state = GameRootState() };
    }

    /// <summary>切换选中的目录。换目录等于换了一整套 versions/libraries，界面必须重新拉。</summary>
    private object GameRootSelect(JsonElement payload)
    {
        string id = Required(payload, "id");

        if (Settings.GameRoots.All(root => root.Id != id))
        {
            return new { ok = false, error = "这个目录不在列表里。" };
        }

        _settings.Update(current => current with { ActiveGameRootId = id });
        _paths = new GamePaths(ResolveGameRoot());

        Emit("settings.changed", new { settings = Settings });

        return new { ok = true, state = GameRootState() };
    }

    /// <summary>用系统默认浏览器打开链接。只放行 http/https。</summary>
    private static object ShellOpenUrl(JsonElement payload)
    {
        string url = Required(payload, "url");

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new { ok = false, error = "只支持 http/https 链接。" };
        }

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();

        return new { ok = true };
    }

    /// <summary>弹系统的文件夹选择框。取消不是错误，前端按 canceled 处理。</summary>
    private object ShellPickFolder()
    {
        if (_pickFolder is null)
        {
            return new { ok = false, error = "这个宿主不支持选择目录。" };
        }

        string? picked = _pickFolder();

        return string.IsNullOrWhiteSpace(picked)
            ? new { ok = true, canceled = true, path = (string?)null }
            : new { ok = true, canceled = false, path = picked };
    }

    /// <summary>
    /// 用户同意某个插件的权限。同意完立刻重扫——同意这件事的意义就是"现在可以加载了"。
    /// </summary>
    private object PluginGrant(JsonElement payload)
    {
        string id = Required(payload, "id");

        var permissions = new List<string>();
        if (payload.TryGetProperty("permissions", out JsonElement array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                {
                    permissions.Add(text);
                }
            }
        }

        var plugins = _plugins.Grant(id, permissions);

        Emit("log", new
        {
            level = "info",
            instance = (string?)null,
            text = $"已允许插件 {id} 使用：{(permissions.Count == 0 ? "（无）" : string.Join(" ", permissions))}"
        });

        return new { ok = true, root = _plugins.Root, plugins = plugins.Select(ToPluginView).ToList() };
    }

    /// <summary>把已加载插件的界面片段交给页面。页面按 slot 挂到对应容器。</summary>
    private object PluginUi() => new
    {
        ok = true,
        ui = _plugins.UiContributions.Select(item => new
        {
            pluginId = item.PluginId,
            slot = item.Slot,
            html = item.Html,
            script = item.Script,
            title = item.Title,
            // 插件要顶掉的宿主控件 id：前端据此隐藏，下次加载时再还原
            replaces = item.Replaces,
            // 图标交给页面直接用：静态资源路由只认插件目录内部
            icon = item.IconPath is null
                ? null
                : $"/plugin/{item.PluginId}/{item.IconPath.Replace(Path.DirectorySeparatorChar, '/')}"
        }).ToList()
    };

    /// <summary>界面上那块设置片段打开时先拿一次初值。</summary>
    private object PluginSettingsGet(JsonElement payload)
    {
        string id = Required(payload, "id");
        return new { ok = true, settings = _plugins.SettingsFor(id).All };
    }

    private object PluginSettingsSet(JsonElement payload)
    {
        string id = Required(payload, "id");
        string key = Required(payload, "key");

        string? value = payload.TryGetProperty("value", out JsonElement element)
                        && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

        _plugins.SettingsFor(id).Set(key, value);
        return new { ok = true };
    }

    /// <summary>页面上的按钮 → 插件自己的代码。插件没实现 IPluginCommand 就如实报错。</summary>
    private async Task<object> PluginCommandAsync(JsonElement payload)
    {
        string id = Required(payload, "id");
        string command = Required(payload, "command");

        string? data = payload.TryGetProperty("payload", out JsonElement element)
                       && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

        try
        {
            string? result;

            // 标一下"现在是这个插件在跑"：万一它把进程带崩了，崩溃记录里能指认它。
            using (_crash.EnterPlugin(id))
            {
                result = await _plugins
                    .ExecuteCommandAsync(id, command, data, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            LogPluginCommand(id, $"command:{command}", "ok");
            return new { ok = true, result };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPluginCommand(id, $"command:{command}", "failed");
            return new { ok = false, error = ex.Message };
        }
    }

    /// <summary>插件命令也留一条日志：谁在什么时候让插件干了什么。</summary>
    private void LogPluginCommand(string id, string action, string result) =>
        Emit("log", new { level = "info", instance = (string?)null, text = $"插件命令 {id} {action} → {result}" });

    private object PluginReload()
    {
        IReadOnlyList<PluginInfo> plugins = _plugins.Refresh();

        // 文件被换过的插件：热加载虽然生效了，但旧实例的静态状态不一定收干净，
        // 所以"要不要提醒重启"交给设置（默认提醒）。
        IReadOnlyList<string> changed = _plugins.LastChanged;
        bool warn = Settings.WarnRestartAfterPluginChange != false && changed.Count > 0;

        return new
        {
            ok = true,
            root = _plugins.Root,
            plugins = plugins.Select(ToPluginView).ToList(),
            changed,
            restartSuggested = warn
        };
    }

    private static object ToPluginView(PluginInfo plugin) => new
    {
        id = plugin.Id,
        name = plugin.Name,
        version = plugin.Version,
        type = plugin.Type,
        // 头像由插件自己在清单里指定；静态路由只认插件目录内部
        icon = plugin.Icon is null
            ? null
            : $"/plugin/{plugin.Id}/{plugin.Icon.Replace(Path.DirectorySeparatorChar, '/')}",
        directory = plugin.Directory,
        state = plugin.State,
        enabled = plugin.Enabled,
        error = plugin.Error,
        warnings = plugin.Warnings,
        permissions = plugin.Permissions,
        missingPermissions = plugin.MissingPermissions
    };

    /// <summary>
    /// 用默认浏览器打开一个外链（现在的用途：弹窗里的「购买 Minecraft」）。
    /// 只放行 http/https：不让界面借这条命令去启动本地程序。
    /// </summary>
    private object ShellOpen(JsonElement payload)
    {
        string url = Required(payload, "url");

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new { ok = false, error = "只支持 http / https 链接。" };
        }

        return new { ok = TryOpenBrowser(url) };
    }

    /// <summary>
    /// 把文字放进剪贴板。
    /// </summary>
    /// <remarks>
    /// <para>
    /// WinForms 的 <see cref="Clipboard"/> 只能在 STA 线程上用，而这个方法经常是从
    /// <c>await</c> 之后的线程池线程调到的——那里调用会抛 <c>ThreadStateException</c>，
    /// 之前被我静默吞掉，于是"自动复制"看起来做了其实没做。所以这里自己起一个 STA 线程。
    /// </para>
    /// <para>
    /// 剪贴板还可能被别的程序占用（OpenClipboard 失败），老办法是重试几次，间隔很短。
    /// </para>
    /// </remarks>
    private static bool TryCopyToClipboard(string text)
    {
        bool copied = false;

        var worker = new Thread(() =>
        {
            for (int attempt = 0; attempt < 5 && !copied; attempt++)
            {
                try
                {
                    Clipboard.SetText(text);
                    copied = true;
                }
                catch (Exception ex) when (ex is ExternalException or ThreadStateException or InvalidOperationException)
                {
                    Thread.Sleep(40);
                }
            }
        });

        worker.SetApartmentState(ApartmentState.STA);
        worker.IsBackground = true;
        worker.Start();
        worker.Join(TimeSpan.FromSeconds(2));

        return copied;
    }

    // ── 更新 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 检查更新。失败也要回一条**结构化**结论（<c>ok:true</c> + <c>message</c>），
    /// 不能抛：更新源配错、服务器挂了、压根没配，都是日常状态，不是异常。
    /// </summary>
    private static async Task<object> UpdateCheckAsync(CancellationToken cancellationToken)
    {
        UpdateStatus status = await UpdateService.CheckAsync(cancellationToken).ConfigureAwait(false);
        return new
        {
            ok = true,
            status.Checked,
            status.Available,
            status.CurrentVersion,
            status.NewVersion,
            status.CanApply,
            status.Message,
            feed = UpdateService.FeedDescription
        };
    }

    private async Task<object> UpdateApplyAsync(CancellationToken cancellationToken)
    {
        Emit("log", new { level = "info", text = "开始下载更新…" });

        UpdateStatus status = await UpdateService.ApplyAsync(
            percent => Emit("update.progress", new { percent }),
            cancellationToken).ConfigureAwait(false);

        Emit("log", new { level = status.Available ? "info" : "warn", text = status.Message });

        return new
        {
            ok = true,
            status.Available,
            status.CurrentVersion,
            status.NewVersion,
            status.Message
        };
    }
    // ── 设置 ─────────────────────────────────────────────────────────

    private object SettingsGet()
    {
        // 目录列表跟着设置一起发：前端一次拿全，不用再多打一次 gameRoot.list
        return new
        {
            ok = true,
            settings = Settings,
            hasGameRoot = Settings.HasGameRoot,
            gameRoot = _paths.Root,
            gameRoots = Settings.GameRoots.Select(root => new
            {
                id = root.Id,
                note = root.Note,
                path = root.Path,
                exists = Directory.Exists(root.Path)
            }).ToArray(),
            activeGameRootId = Settings.ActiveGameRoot?.Id,
            settingsFile = _settings.FilePath,
            broken = File.Exists(_settings.BrokenFilePath)
        };
    }

    /// <summary>
    /// 改设置。**只处理 payload 里出现的字段**，没出现的保持原值——
    /// 否则前端改一个「内存」会把「游戏目录」清掉。
    /// </summary>
    private object SettingsUpdate(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return new { ok = false, error = "settings.update 需要一个对象。" };
        }

        LauncherSettings updated = _settings.Update(current =>
        {
            var next = current;

            if (payload.TryGetProperty("gameRoot", out var gameRoot))
            {
                next = next with { GameRoot = gameRoot.GetString() };
            }

            if (payload.TryGetProperty("playerName", out var playerName))
            {
                next = next with { PlayerName = playerName.GetString() ?? "" };
            }

            if (payload.TryGetProperty("memoryMb", out var memory) && memory.TryGetInt32(out int mb))
            {
                next = next with { MemoryMb = mb };
            }

            if (payload.TryGetProperty("windowWidth", out var w) && w.TryGetInt32(out int width))
            {
                next = next with { WindowWidth = width };
            }

            if (payload.TryGetProperty("windowHeight", out var h) && h.TryGetInt32(out int height))
            {
                next = next with { WindowHeight = height };
            }

            if (payload.TryGetProperty("javaOrder", out var javaOrder)
                && javaOrder.ValueKind == JsonValueKind.Array)
            {
                var order = new List<string>();

                foreach (JsonElement item in javaOrder.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } path)
                    {
                        order.Add(path);
                    }
                }

                next = next with { JavaOrder = order };
            }

            if (payload.TryGetProperty("extraGameArguments", out var extraGame)
                && extraGame.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();

                foreach (JsonElement item in extraGame.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                    {
                        list.Add(text);
                    }
                }

                next = next with { ExtraGameArguments = list };
            }

            if (payload.TryGetProperty("preLaunchCommand", out var preLaunch))
            {
                next = next with { PreLaunchCommand = preLaunch.GetString() };
            }

            if (payload.TryGetProperty("gcPreset", out var gcPreset))
            {
                next = next with { GcPreset = gcPreset.GetString() ?? LauncherSettings.GcPresets.Default };
            }

            if (payload.TryGetProperty("disableLaunchWrapper", out var disableWrapper)
                && disableWrapper.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                next = next with { DisableLaunchWrapper = disableWrapper.GetBoolean() };
            }

            if (payload.TryGetProperty("checkFilesBeforeLaunch", out var checkFiles)
                && checkFiles.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                next = next with { CheckFilesBeforeLaunch = checkFiles.GetBoolean() };
            }

            if (payload.TryGetProperty("autoRepairMissing", out var autoRepair)
                && autoRepair.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                next = next with { AutoRepairMissing = autoRepair.GetBoolean() };
            }

            if (payload.TryGetProperty("warnRestartAfterPluginChange", out var warnRestart)
                && warnRestart.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                next = next with { WarnRestartAfterPluginChange = warnRestart.GetBoolean() };
            }

            if (payload.TryGetProperty("autoJoinServer", out var joinServer) && joinServer.ValueKind == JsonValueKind.String)
            {
                next = next with { AutoJoinServer = joinServer.GetString() };
            }

            if (payload.TryGetProperty("downloadThreads", out var threads) && threads.TryGetInt32(out int threadCount))
            {
                next = next with { DownloadThreads = threadCount };
            }

            if (payload.TryGetProperty("downloadSpeedLimitMb", out var speed) && speed.TryGetInt32(out int speedMb))
            {
                next = next with { DownloadSpeedLimitMb = speedMb };
            }

            if (payload.TryGetProperty("downloadParallelTasks", out var parallel) && parallel.TryGetInt32(out int parallelCount))
            {
                next = next with { DownloadParallelTasks = parallelCount };
            }

            if (payload.TryGetProperty("diagnosticLog", out var diagnostic)
                && diagnostic.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                next = next with { DiagnosticLog = diagnostic.GetBoolean() };
            }

            if (payload.TryGetProperty("preferHighPerformanceGpu", out var preferGpu)
                && preferGpu.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                next = next with { PreferHighPerformanceGpu = preferGpu.GetBoolean() };
            }

            if (payload.TryGetProperty("useCustomMemory", out var customMemory)
                && customMemory.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                next = next with { UseCustomMemory = customMemory.GetBoolean() };
            }

            if (payload.TryGetProperty("memoryOptimize", out var memoryOptimize)
                && memoryOptimize.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                next = next with { MemoryOptimize = memoryOptimize.GetBoolean() };
            }

            if (payload.TryGetProperty("gamePriority", out var priority))
            {
                next = next with { GamePriority = priority.GetString() ?? LauncherSettings.GamePriorities.Normal };
            }

            if (payload.TryGetProperty("gameWindowMode", out var windowMode))
            {
                next = next with { GameWindowMode = windowMode.GetString() ?? LauncherSettings.GameWindowModes.Default };
            }

            if (payload.TryGetProperty("launcherVisibility", out var visibility))
            {
                next = next with { LauncherVisibility = visibility.GetString() ?? LauncherSettings.LauncherVisibilities.Keep };
            }

            if (payload.TryGetProperty("javaHome", out var javaHome))
            {
                next = next with { JavaHome = javaHome.GetString() };
            }

            if (payload.TryGetProperty("microsoftClientId", out var microsoftClientId))
            {
                next = next with { MicrosoftClientId = microsoftClientId.GetString() };
            }

            if (payload.TryGetProperty("lastVersionId", out var lastVersion))
            {
                next = next with { LastVersionId = lastVersion.GetString() };
            }

            if (payload.TryGetProperty("extraJvmArguments", out var extra) && extra.ValueKind == JsonValueKind.Array)
            {
                next = next with
                {
                    ExtraJvmArguments = [.. extra.EnumerateArray()
                        .Select(item => item.GetString())
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Select(item => item!)]
                };
            }

            return next;
        });

        // 游戏目录可能变了，视图必须立刻跟着换，否则界面还在列旧目录的版本。
        _paths = new GamePaths(ResolveGameRoot());
        ApplyDownloadSettings(updated);

        Emit("settings.changed", new { settings = updated });

        return new { ok = true, settings = updated, gameRoot = _paths.Root, hasGameRoot = updated.HasGameRoot };
    }

    /// <summary>扫本机已存在的游戏目录，供首次启动时一键选用。</summary>
    /// <summary>设置页那条内存条要的数据：总量、已用、以及"自动配置"会给出多少。</summary>
    private static object MemoryInfo()
    {
        SystemMemoryInfo memory = SystemMemoryInfo.Read();
        int autoMb = memory.TotalMb > 0
            ? MemoryPlanner.RecommendMb(memory.TotalMb, memory.AvailableMb, modCount: 0)
            : 0;

        return new
        {
            ok = true,
            totalMb = memory.TotalMb,
            usedMb = memory.UsedMb,
            availableMb = memory.AvailableMb,
            autoMb
        };
    }

    private static object SettingsDetect()
    {
        var candidates = GameRootLocator.Detect();

        return new
        {
            ok = true,
            candidates = candidates.Select(candidate => new
            {
                path = candidate.Path,
                source = candidate.Source,
                valid = candidate.LooksValid,
                versions = candidate.VersionCount
            }).ToArray(),
            best = GameRootLocator.Best(candidates)?.Path
        };
    }

    // ── 版本（只扫本地） ──────────────────────────────────────────────

    /// <summary>
    /// 列出<strong>本机已存在</strong>的版本。
    /// </summary>
    /// <remarks>
    /// 这里刻意不联网。内核的边界是「从磁盘上已有的东西启动」：
    /// 一份 version.json 都不在本地时，启动器唯一的诚实回答是「没有可启动的版本」，
    /// 而不是替用户去网上找——找版本、下文件、装东西是安装器插件的活。
    /// </remarks>
    /// <summary>给插件用的下载入口：进同一个队列，所以下载页看得见。</summary>
    internal string? SubmitDownload(string title, IReadOnlyList<DownloadItem> items) =>
        _downloads.Enqueue(title, items);

    internal Task<bool> WaitForDownloadAsync(string taskId, CancellationToken cancellationToken) =>
        _downloads.WaitForAsync(taskId, cancellationToken);

    internal HostDownloadStatus? DownloadStatus(string taskId)
    {
        DownloadTaskSnapshot? snapshot = _downloads.Snapshot().FirstOrDefault(task => task.Id == taskId);

        return snapshot is null
            ? null
            : new HostDownloadStatus(
                snapshot.Id,
                snapshot.State,
                snapshot.TotalBytes,
                snapshot.DoneBytes,
                snapshot.FilesTotal,
                snapshot.FilesDone,
                snapshot.Error);
    }

    /// <summary>列 Mojang 清单里的版本。给安装器插件用。</summary>
    internal async Task<IReadOnlyList<HostRemoteVersion>> ListRemoteVersionsAsync(
        bool includeSnapshots,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        var client = new VersionManifestClient(http);
        VersionManifest manifest = await client.FetchAsync(cancellationToken).ConfigureAwait(false);

        return [.. manifest.Versions
            .Where(entry => includeSnapshots || entry.IsRelease)
            .Select(entry => new HostRemoteVersion(entry.Id, entry.Type, entry.ReleaseTime))];
    }

    /// <summary>装一个版本到当前游戏目录。给安装器插件用。</summary>
    internal async Task<HostInstallResult> InstallVersionAsync(
        string versionId,
        IProgress<HostInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        var client = new VersionManifestClient(http);
        var downloader = new FileDownloader(http);
        var queue = new DownloadQueue(downloader);
        var installer = new GameInstaller(_paths, queue);
        var versionInstaller = new VersionInstaller(_paths, client, installer);

        var bridged = progress is null
            ? null
            : new Progress<InstallProgress>(report =>
                progress.Report(new HostInstallProgress(report.Stage.ToString(), report.Completed, report.Total, report.BytesDone)));

        // 走队列这条路没有 InstallReport，实际传输量只能自己接住
        long transferred = 0;

        VersionInstallResult result = await versionInstaller
            .InstallAsync(
                versionId,
                CurrentEnvironment(),
                bridged,
                cancellationToken,
                // 安装走下载队列：进度出现在「下载」页，用户能暂停、能限速。
                downloadThrough: async (title, items, healthyBytes, healthyFiles, token) =>
                {
                    string? taskId = _downloads.Enqueue(title, items, healthyBytes, healthyFiles);

                    if (taskId is null)
                    {
                        return (false, 0L);
                    }

                    bool ok = await _downloads.WaitForAsync(taskId, token).ConfigureAwait(false);
                    long bytes = _downloads.Snapshot().FirstOrDefault(task => task.Id == taskId)?.DoneBytes ?? 0;

                    transferred = bytes - healthyBytes;

                    return (ok, transferred);
                })
            .ConfigureAwait(false);

        return new HostInstallResult(
            result.Ok,
            result.VersionId,
            result.Error,
            result.Report?.FilesDownloaded ?? result.DownloadedFiles,
            result.Report?.FilesSkipped ?? 0,
            result.Report?.Bytes ?? transferred);
    }

    private Task<object> VersionListAsync(CancellationToken cancellationToken)
    {
        if (!Settings.HasGameRoot)
        {
            // 没指定游戏目录时不要假装扫过：返回一句能直接显示给用户的话，
            // 前端据此弹「首次启动」面板，而不是显示「0 个版本」让用户以为游戏没了。
            return Task.FromResult<object>(new
            {
                ok = false,
                needsGameRoot = true,
                root = _paths.Versions,
                error = "还没指定游戏目录。先选一个 .minecraft 文件夹。",
                versions = Array.Empty<object>()
            });
        }

        var versions = new List<(string Id, string? Type, string? InheritsFrom)>();

        if (Directory.Exists(_paths.Versions))
        {
            foreach (string directory in Directory.EnumerateDirectories(_paths.Versions))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string id = Path.GetFileName(directory);
                string jsonPath = _paths.VersionJson(id);
                if (!File.Exists(jsonPath))
                {
                    continue;
                }

                string? type = null;
                string? inheritsFrom = null;
                try
                {
                    var metadata = JsonSerializer.Deserialize<VersionMetadata>(File.ReadAllText(jsonPath));
                    type = metadata?.Type;
                    inheritsFrom = metadata?.InheritsFrom;
                }
                catch (JsonException)
                {
                    type = "broken";
                }

                versions.Add((id, type, inheritsFrom));
            }
        }

        return Task.FromResult<object>(new
        {
            ok = true,
            root = _paths.Versions,
            versions = versions
                .OrderByDescending(v => v.Id, StringComparer.OrdinalIgnoreCase)
                .Select(v => new { id = v.Id, type = v.Type, inheritsFrom = v.InheritsFrom })
                .ToArray()
        });
    }

    // ── 实例 ─────────────────────────────────────────────────────────

    private object InstanceList()
    {
        var scan = _instances.Scan();

        return new
        {
            ok = true,
            instances = scan.Instances.Select(instance => new
            {
                name = instance.Metadata.Name,
                versionId = instance.Metadata.VersionId,
                lastPlayed = instance.Metadata.LastPlayedAt,
                memory = instance.Metadata.MaxMemoryMb,
                width = instance.Metadata.WindowWidth,
                height = instance.Metadata.WindowHeight,
                player = instance.Metadata.PlayerName,
                accountId = instance.Metadata.AccountId,
                installed = File.Exists(_paths.VersionJson(instance.Metadata.VersionId))
                    && File.Exists(_paths.ClientJar(instance.Metadata.VersionId))
            }).ToArray(),
            warnings = scan.Warnings
        };
    }

    private object CreateInstance(JsonElement payload)
    {
        string name = Required(payload, "name");
        string versionId = Required(payload, "versionId");

        if (!Settings.HasGameRoot)
        {
            return new { ok = false, needsGameRoot = true, error = "还没指定游戏目录。" };
        }

        var instance = _instances.Create(name, versionId);

        // 新建实例继承全局设置：用户改过「内存 8G」，新建的实例就该是 8G。
        // 实例里的值仍然是权威的——用户之后可以单独调这个实例，不影响别人。
        var m = instance.Metadata;
        m.MaxMemoryMb = Settings.MemoryMb;
        m.WindowWidth = Settings.WindowWidth;
        m.WindowHeight = Settings.WindowHeight;
        m.PlayerName = Settings.PlayerName;
        instance.Save();

        _settings.Update(current => current with { LastVersionId = versionId });

        return new
        {
            ok = true,
            name = instance.Metadata.Name,
            versionId = instance.Metadata.VersionId,
            memory = m.MaxMemoryMb,
            player = m.PlayerName
        };
    }

    private object UpdateInstance(JsonElement payload)
    {
        string name = Required(payload, "name");
        var instance = _instances.Find(name)
            ?? throw new InvalidOperationException($"实例「{name}」不存在。");

        var m = instance.Metadata;

        if (payload.TryGetProperty("memory", out var memory) && memory.TryGetInt32(out int mb) && mb > 0)
        {
            m.MaxMemoryMb = mb;
        }

        if (payload.TryGetProperty("width", out var width) && width.TryGetInt32(out int w) && w > 0)
        {
            m.WindowWidth = w;
        }

        if (payload.TryGetProperty("height", out var height) && height.TryGetInt32(out int h) && h > 0)
        {
            m.WindowHeight = h;
        }

        if (payload.TryGetProperty("player", out var player) && player.GetString() is { Length: > 0 } p)
        {
            m.PlayerName = p;
        }

        // 空字符串表示「不指定账号，用最近使用/实例玩家名」——不能写成空 id 留着。
        if (payload.TryGetProperty("accountId", out var accountId))
        {
            m.AccountId = string.IsNullOrWhiteSpace(accountId.GetString()) ? null : accountId.GetString();
        }

        instance.Save();

        return new { ok = true };
    }

    private object DeleteInstance(JsonElement payload)
    {
        string name = Required(payload, "name");
        string? trashed = _instances.Delete(name);

        return new { ok = true, trashedTo = trashed };
    }

    // ── 安装（不在内核里） ───────────────────────────────────────────

    /// <summary>
    /// 安装不属于内核。这里明确回错误，而不是偷偷留一条能用的下载路径。
    /// </summary>
    /// <remarks>
    /// 内核的边界是「从磁盘上已有的东西启动」。下载、校验、装模组加载器全要求内核认识
    /// 网络与 CDN；一旦开这个口子，镜像源、断点续传、整合包、模组更新会一个一个跟着搬进来，
    /// 内核就退回单体启动器了。
    /// <para>
    /// 界面据此显示「需要安装器插件」，用户不会以为程序坏了。
    /// </para>
    /// </remarks>
    private static Task<object> StartInstallAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        string versionId = payload.TryGetProperty("versionId", out var value)
            && value.GetString() is { Length: > 0 } id
            ? id
            : "(未指定)";

        return Task.FromResult<object>(new
        {
            ok = false,
            pluginRequired = "installer",
            versionId,
            error = "安装功能不属于启动器内核。要下载库、客户端与资源，需要装一个安装器插件。"
        });
    }


    // ── 启动 ─────────────────────────────────────────────────────────

    private async Task<object> StartLaunchAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        string name = Required(payload, "name");

        lock (_runningGate)
        {
            if (_running.ContainsKey(name))
            {
                return new { ok = false, error = $"实例「{name}」已经在运行。" };
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await LaunchAsync(name).ConfigureAwait(false);
            }
            finally
            {
                // 运行状态由 LaunchAsync 自己维护，这里不用管。
            }
        }, CancellationToken.None);

        return new { ok = true, started = true, name };
    }

    private async Task LaunchAsync(string name)
    {
        // 每个实例一个取消句柄：杀掉某一个不会连累别的。
        using var token = new CancellationTokenSource();
        lock (_runningGate)
        {
            _running[name] = token;
        }

        // 0 = 还没拿到闸门，1 = 拿到了，2 = 已放行。spawn 回调和 finally 都可能来放行，
        // 用状态机保证只 Release 一次。
        int gateState = 0;

        void ReleaseGate()
        {
            if (Interlocked.CompareExchange(ref gateState, 2, 1) == 1)
            {
                _startGate.Release();
            }
        }

        try
        {
            await _startGate.WaitAsync(token.Token).ConfigureAwait(false);
            Interlocked.Exchange(ref gateState, 1);

            // 拿到闸门 = 真的要开工了。启动动画之类的插件在这一刻该动起来：
            // 比 launch.started 早（那时进程已经起来），比点按钮晚（那时还没排上队）。
            RaisePluginEvent("launch.preparing", new { instance = name });

            var instance = _instances.Find(name)
                ?? throw new InvalidOperationException($"实例「{name}」不存在。");

            var m = instance.Metadata;
            string versionPath = _paths.VersionJson(m.VersionId);

            if (!File.Exists(versionPath))
            {
                throw new InvalidOperationException($"版本 {m.VersionId} 还没装。");
            }

            var version = JsonSerializer.Deserialize<VersionMetadata>(await File.ReadAllTextAsync(versionPath))
                ?? throw new InvalidDataException($"版本元数据为空：{versionPath}");

            // 启动前检查：只 stat + 比大小（快），缺了才补。
            // 补的是"缺的那几个"，不是跑一遍装机流程——那样每个文件都要算 SHA1，慢得多。
            await VerifyFilesBeforeLaunchAsync(name, version).ConfigureAwait(false);

            var installation = ResolveJava(version);
            Emit("log", new
            {
                level = "info",
                instance = name,
                text = $"Java {installation.Version.Raw}：{installation.Home}"
            });

            // natives 必须在起 JVM 之前就位。
            // 1.19 起 natives 是独立的 classifier jar（不是把 dll 塞在同一个 jar 里），
            // 别的启动器装好的版本不会替我们解压，而 -Djava.library.path 又指着这个目录：
            // 少了这一步，游戏会一路跑到 "Setting user" 才在 [LWJGL] Failed to load a library 处卡死。
            // 解压是幂等的（有清单，jar 集合没变就整轮跳过），所以每次启动都调是安全的。
            NativesPlan nativesPlan = NativesPlanner.Build(_paths, version.Libraries ?? [], CurrentEnvironment());
            NativesExtractResult natives = NativesExtractor.Extract(_paths, m.VersionId, nativesPlan);

            if (nativesPlan.Entries.Count > 0 && natives.Files.Count == 0)
            {
                // 有 jar 却一个文件都没解出来，就是真的缺东西。这时候起 JVM 只会得到
                // UnsatisfiedLinkError，指的方向还是错的——直接说不缺什么。
                string reason = natives.Warnings.FirstOrDefault() ?? "没有可用的本地库文件";
                throw new InvalidOperationException(
                    $"natives 没能解压（计划 {nativesPlan.Entries.Count} 个 jar）：{reason}");
            }

            Emit("log", new
            {
                level = "info",
                instance = name,
                text = $"natives：{natives.Files.Count} 个文件"
                       + $"（{nativesPlan.Entries.Count} 个 jar，按架构跳过 {nativesPlan.SkippedByArchitecture.Count} 个）"
            });

            foreach (string warning in nativesPlan.Warnings.Take(3))
            {
                Emit("log", new { level = "warn", instance = name, text = $"natives：{warning}" });
            }

            foreach (string warning in natives.Warnings.Take(3))
            {
                Emit("log", new { level = "warn", instance = name, text = $"natives：{warning}" });
            }

            // 实例上选了账号就用它；没选则用账号簿「最近使用」；都没有就退回实例的离线名。
            LaunchAccount account = await _accounts
                .ResolveAsync(m.AccountId, m.PlayerName, CancellationToken.None)
                .ConfigureAwait(false);

            Emit("log", new
            {
                level = account.IsOffline && _accounts.LastFallbackReason is not null ? "warn" : "info",
                instance = name,
                text = account.IsOffline
                    ? (_accounts.LastFallbackReason is null
                        ? $"离线账号：{account.PlayerName}"
                        : $"正版登录已失效（{_accounts.LastFallbackReason}），改用离线账号：{account.PlayerName}")
                    : $"正版账号：{account.PlayerName}"
            });

            // 插件在装配命令之前插手：可以追加参数，不能改启动器已经定下的东西。
            // 请求对象是可变的白名单（只有两个列表），插件拿不到实例、账号、路径这些内部状态。
            var pluginRequest = new LaunchRequest(
                name,
                m.VersionId,
                account.PlayerName,
                instance.GameDirectory,
                [.. GcFlags(Settings.GcPreset, installation), .. Settings.ExtraJvmArguments, .. m.ExtraJvmArguments],
                [.. Settings.ExtraGameArguments, .. m.ExtraGameArguments]);

            int jvmBefore = pluginRequest.JvmArguments.Count;
            int gameBefore = pluginRequest.GameArguments.Count;

            // 守卫能拒绝这次启动（启动器口令、内存不足、缺文件都属这一类）。
            // 返回的原因会原样显示给用户，所以插件要写人话。
            string? denied = _plugins.ApplyLaunchHooks(pluginRequest);

            if (denied is not null)
            {
                // 复用 launch.done 的失败分支：界面本来就认识它，不用再教它一个新事件
                Emit("launch.done", new { instance = name, ok = false, error = $"被插件拦下：{denied}" });
                return;
            }

            int jvmAdded = pluginRequest.JvmArguments.Count - jvmBefore;
            int gameAdded = pluginRequest.GameArguments.Count - gameBefore;

            if (jvmAdded > 0 || gameAdded > 0)
            {
                // 把追加的内容打出来：只说"追加了 2 条"没法排查是哪个插件加的什么。
                string added = string.Join(" ", pluginRequest.JvmArguments.Skip(jvmBefore).Take(jvmAdded)
                    .Concat(pluginRequest.GameArguments.Skip(gameBefore).Take(gameAdded)));

                Emit("log", new
                {
                    level = "info",
                    instance = name,
                    text = $"插件追加了 {jvmAdded} 条 JVM 参数、{gameAdded} 条游戏参数：{added}"
                });
            }

            // 游戏窗口大小：五种模式里只有 custom 用实例自己的宽高，
            // 其余由设置决定；launcher / maximized 需要窗口层提供的尺寸。
            string windowMode = Settings.GameWindowMode;

            if (windowMode == LauncherSettings.GameWindowModes.FullScreen)
            {
                pluginRequest.AddGameArgument("--fullscreen");
            }

            // 启动后自动进服：MC 只认一组 --server/--port，且只在启动那一次连
            if (Settings.AutoJoinServer is { Length: > 0 } server)
            {
                (string host, int port) = ParseServer(server);

                pluginRequest.AddGameArgument("--server");
                pluginRequest.AddGameArgument(host);
                pluginRequest.AddGameArgument("--port");
                pluginRequest.AddGameArgument(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            (int Width, int Height)? windowSize = windowMode switch
            {
                LauncherSettings.GameWindowModes.FullScreen => null,
                LauncherSettings.GameWindowModes.Default => null,
                LauncherSettings.GameWindowModes.Launcher => LauncherWindowSize,
                LauncherSettings.GameWindowModes.Maximized => ScreenWorkArea,
                _ => (m.WindowWidth, m.WindowHeight)
            };

            // Java 18 以前的版本在中文路径下起不来：把路径换成 8.3 短路径。
            // 这是 PCL 那个 wrapper 干的事，我们不带额外 exe，用系统自带的能力做。
            (string javaExecutable, string gameDirectory) = ResolvePathsForLegacyJava(installation, instance, name);

            // 内存要在拼命令之前定下来：--xmx 是命令的一部分。
            (int memoryMb, _, bool memoryClamped) = ResolveMemoryMb(instance);

            if (memoryClamped)
            {
                Emit("log", new
                {
                    level = "warn",
                    instance = name,
                    text = $"设置的内存超过本机物理内存，已压到 {memoryMb} MB（上限 = 物理内存减 1 GB）"
                });
            }

            var command = LaunchCommandBuilder.Build(new LaunchOptions
            {
                Paths = _paths,
                Version = version,
                Environment = CurrentEnvironment(),
                JavaExecutable = javaExecutable,
                GameDirectory = gameDirectory,
                Account = account,
                LauncherName = "ML",
                LauncherVersion = LauncherVersion,
                MaxMemoryMb = memoryMb,
                // 日志配置的 id 每个版本都可能不同（1.12 是 client-1.12.xml，1.20 还是它，
                // 但这是元数据说的，不是我们该记住的），所以取 version.logging.client.file.id。
                Log4jConfigPath = Log4jConfigPathFor(version),
                WindowSize = windowSize,
                ExtraJvmArguments = pluginRequest.JvmArguments,
                ExtraGameArguments = pluginRequest.GameArguments
            });

            if (command.MissingClasspathFiles.Count > 0)
            {
                // 有文件缺失就到此为止：起 JVM 只会得到一句指错方向的 NoClassDefFoundError。
                throw new InvalidOperationException(
                    $"classpath 里 {command.MissingClasspathFiles.Count} 个文件不存在，重装这个版本："
                    + Path.GetFileName(command.MissingClasspathFiles[0]));
            }

            Directory.CreateDirectory(instance.GameDirectory);


            var watch = Stopwatch.StartNew();
            if (Settings.PreferHighPerformanceGpu)
            {
                int written = GpuPreference.PreferHighPerformance(
                    [Environment.ProcessPath ?? string.Empty, installation.ExecutablePath]);

                Emit("log", new
                {
                    level = written > 0 ? "info" : "warn",
                    instance = name,
                    text = written > 0
                        ? $"已把启动器与 Java 设为使用高性能显卡（写入 {written} 项）"
                        : "写入显卡偏好失败或无需写入（单显卡机器可忽略）"
                });
            }

            if (Settings.PreLaunchCommand is { Length: > 0 } preLaunch)
            {
                RunPreLaunchCommand(name, preLaunch, instance.GameDirectory);
            }

            if (Settings.MemoryOptimize)
            {
                var (tried, trimmed) = MemoryOptimizer.TrimWorkingSets();
                Emit("log", new
                {
                    level = "info",
                    instance = name,
                    text = $"内存优化：收缩了 {trimmed}/{tried} 个进程的工作集（不是真的释放物理内存）"
                });
            }

            Emit("log", new
            {
                level = "info",
                instance = name,
                text = $"游戏进程：优先级 {Settings.GamePriority}，窗口 {windowMode}"
                       + (windowSize is { } size ? $"（{size.Width}×{size.Height}）" : "（不传宽高）")
            });

            Emit("log", new
            {
                level = "info",
                instance = name,
                text = $"游戏内存：{memoryMb} MB（{(Settings.UseCustomMemory ? "自定义" : "自动配置")}）"
            });

            // 用户自己填的参数：有没有拼对、生不生效，只能靠日志自证
            if (pluginRequest.JvmArguments.Count > 0)
            {
                Emit("log", new
                {
                    level = "info",
                    instance = name,
                    text = $"JVM 参数：{string.Join(' ', pluginRequest.JvmArguments)}"
                });
            }

            if (pluginRequest.GameArguments.Count > 0)
            {
                Emit("log", new
                {
                    level = "info",
                    instance = name,
                    text = $"游戏参数：{string.Join(' ', pluginRequest.GameArguments)}"
                });
            }

            ProcessPriorityClass? priority = Settings.GamePriority switch
            {
                LauncherSettings.GamePriorities.High => ProcessPriorityClass.High,
                LauncherSettings.GamePriorities.Low => ProcessPriorityClass.BelowNormal,
                _ => ProcessPriorityClass.Normal
            };

            // 落盘：游戏输出只在界面上滚，关掉就没了。
            // 起不来的时候（Java 版本不对、库缺失）MC 自己不会写 latest.log，这里是唯一证据。
            GameLogFile logFile = GameLogFile.Create(Path.Combine(DataRoot, "logs"), name, DateTimeOffset.Now);
            _gameLogs[name] = logFile;

            if (Settings.DiagnosticLog)
            {
                logFile.WriteBlock(BuildDiagnosticPreamble(name, instance, command, memoryMb));
            }

            var outcome = await GameProcessRunner.RunAsync(
                command,
                timeout: LaunchTimeout(),
                onLine: line =>
                {
                    logFile.Write("game", line);
                    Emit("log", new { level = "game", instance = name, text = line });
                },
                priority: priority,
                onStarted: _ =>
                {
                    // 进程起来了 = 启动阶段结束，放行下一个实例；窗口动作也放在这时候。
                    ReleaseGate();
                    RaisePluginEvent("launch.started", new { instance = name, versionId = m.VersionId });
                    ApplyLauncherVisibility(gameStarted: true);
                },
                cancellationToken: token.Token).ConfigureAwait(false);

            watch.Stop();

            logFile.Write("launcher", $"游戏进程结束，退出码 {outcome.ExitCode}，用时 {watch.Elapsed.TotalSeconds:F1} 秒");
            logFile.Dispose();
            _gameLogs.TryRemove(name, out _);

            instance.MarkPlayed(DateTimeOffset.Now);

            ApplyLauncherVisibility(gameStarted: false);
            RaisePluginEvent("launch.exited", new { instance = name, exitCode = outcome.ExitCode });

            Emit("launch.done", new
            {
                instance = name,
                exitCode = outcome.ExitCode,
                diagnosis = outcome.Diagnosis.ToString(),
                seconds = Math.Round(watch.Elapsed.TotalSeconds, 1),
                sawStartup = outcome.SawStartupMarker,
                tail = outcome.TailOutput.TakeLast(20).ToArray()
            });
        }
        catch (OperationCanceledException)
        {
            Emit("launch.done", new { instance = name, exitCode = -1, diagnosis = "Killed", seconds = 0, sawStartup = false, tail = Array.Empty<string>() });
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            Emit("launch.done", new
            {
                instance = name,
                ok = false,
                error = $"{ex.GetType().Name}: {ex.Message}",
                exitCode = -1,
                diagnosis = "Failed",
                seconds = 0,
                sawStartup = false,
                tail = Array.Empty<string>()
            });
        }
        finally
        {
            // 启动失败、被取消、正常退出，都走这里：闸门必须放行，运行状态必须摘掉。
            ReleaseGate();

            lock (_runningGate)
            {
                _running.Remove(name);
            }
        }
    }

    /// <summary>
    /// 这次启动用多少堆。自动配置按模组数量算，自定义用实例或全局的值。
    /// 两种都受"本机物理内存减 1 GB"封顶——手改 config.json 写了 20000 MB 也不能真给。
    /// </summary>
    private (int Mb, int Ceiling, bool Clamped) ResolveMemoryMb(Instance instance)
    {
        SystemMemoryInfo memory = SystemMemoryInfo.Read();

        int ceiling = memory.TotalMb > 0
            ? (int)Math.Max(512, memory.TotalMb - 1024)
            : 0;

        if (Settings.UseCustomMemory)
        {
            int configured = instance.Metadata.MaxMemoryMb > 0
                ? instance.Metadata.MaxMemoryMb
                : Settings.MemoryMb;

            if (ceiling > 0 && configured > ceiling)
            {
                return (ceiling, ceiling, true);
            }

            return (Math.Max(512, configured), ceiling, false);
        }

        if (memory.TotalMb <= 0)
        {
            // 读不到物理内存就退回配置值：宁可给一个保守的定值，也不要算出 512 MB 让游戏起不来。
            int fallback = instance.Metadata.MaxMemoryMb > 0 ? instance.Metadata.MaxMemoryMb : Settings.MemoryMb;
            return (fallback, 0, false);
        }

        int auto = MemoryPlanner.RecommendMb(memory.TotalMb, memory.AvailableMb, CountMods(instance));
        return (auto, ceiling, false);
    }


    /// <summary>
    /// Java 18 以前的版本读不了非 ASCII 路径。判定与替换都在 <see cref="LegacyJavaPaths"/> 里，
    /// 这里只负责把结果写进日志。
    /// </summary>
    private (string JavaExecutable, string GameDirectory) ResolvePathsForLegacyJava(
        JavaInstallation installation,
        Instance instance,
        string name)
    {
        LegacyPathResult result = LegacyJavaPaths.Resolve(
            installation.ExecutablePath,
            instance.GameDirectory,
            installation.Version.Major,
            compatibilityEnabled: !Settings.DisableLaunchWrapper,
            linksRoot: Path.Combine(DataRoot, "links"));

        switch (result.Mode)
        {
            case LegacyPathMode.Applied:
                Emit("log", new
                {
                    level = "info",
                    instance = name,
                    text = $"Java {installation.Version.Raw} + 中文路径：已通过目录联接改用 ASCII 路径"
                           + $"（java {result.JavaExecutable}，gameDir {result.GameDirectory}）"
                });
                break;

            case LegacyPathMode.Unavailable:
                Emit("log", new
                {
                    level = "warn",
                    instance = name,
                    text = $"路径含非 ASCII，Java {installation.Version.Raw} 可能起不来，兼容也失败了：{result.Reason}"
                });
                break;
        }

        return (result.JavaExecutable, result.GameDirectory);
    }

    /// <summary>GC 预设对应的 -XX 参数。ZGC 只在 Java 15+ 存在，版本不够就跳过并提示。</summary>
    private IReadOnlyList<string> GcFlags(string preset, JavaInstallation installation)
    {
        switch (preset)
        {
            case LauncherSettings.GcPresets.G1Standard:
                // 只用稳定参数。G1NewSizePercent / G1ReservePercent 在 JDK 21 是 experimental，
                // 不加 UnlockExperimentalVMOptions 会直接 "Could not create the Java Virtual Machine"。
                return ["-XX:+UseG1GC"];

            case LauncherSettings.GcPresets.G1Tuned:
                return ["-XX:+UseG1GC", "-XX:MaxGCPauseMillis=50"];

            case LauncherSettings.GcPresets.Zgc:
                if (installation.Version.Major < 15)
                {
                    Emit("log", new
                    {
                        level = "warn",
                        instance = (string?)null,
                        text = $"当前 Java {installation.Version.Raw} 不支持 ZGC（需要 15+），已忽略 GC 预设"
                    });
                    return [];
                }

                return ["-XX:+UseZGC"];

            case LauncherSettings.GcPresets.ZgcGenerational:
                if (installation.Version.Major < 21)
                {
                    Emit("log", new
                    {
                        level = "warn",
                        instance = (string?)null,
                        text = $"分代 ZGC 需要 Java 21+，当前 {installation.Version.Raw}，已忽略"
                    });
                    return [];
                }

                return ["-XX:+UseZGC", "-XX:+ZGenerational"];

            default:
                return [];
        }
    }

    /// <summary>
    /// 启动前执行用户填的命令。工作目录是实例的 game 目录。
    /// 失败/超时只记日志，不阻止启动——这条命令是辅助手段，不是前置条件。
    /// </summary>
    private void RunPreLaunchCommand(string name, string command, string workingDirectory)
    {
        string executable = System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe");

        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, "/c " + command)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null)
            {
                return;
            }

            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                Emit("log", new { level = "warn", instance = name, text = $"启动前命令超时（30 秒），已终止：{command}" });
                return;
            }

            string text = $"{output.GetAwaiter().GetResult()}{error.GetAwaiter().GetResult()}".Trim();
            string tail = text.Length > 300 ? text[^300..] : text;

            Emit("log", new
            {
                level = process.ExitCode == 0 ? "info" : "warn",
                instance = name,
                text = $"启动前命令退出码 {process.ExitCode}：{command}"
                       + (tail.Length > 0 ? $"，输出：{tail}" : string.Empty)
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Emit("log", new { level = "warn", instance = name, text = $"启动前命令执行失败：{ex.Message}" });
        }
    }

    /// <summary>这个实例装了多少个 mod。自动配置靠它加码。</summary>
    private static int CountMods(Instance instance)
    {
        string mods = Path.Combine(instance.GameDirectory, "mods");

        return Directory.Exists(mods)
            ? Directory.GetFiles(mods, "*.jar").Length
            : 0;
    }

    /// <summary>
    /// 按「启动器可见性」设置控制窗口。窗口不是宿主的东西，所以只发事件，由 Program 动手。
    /// </summary>
    /// <remarks>
    /// 选「立即关闭」时要先 <see cref="DetachGame"/>：否则关窗口 → DisposeAsync 取消 token
    /// → GameProcessRunner 会连进程树一起杀掉，用户看到的是"启动器关了，游戏也没了"。
    /// </remarks>
    private void ApplyLauncherVisibility(bool gameStarted)
    {
        string mode = Settings.LauncherVisibility;
        string? action = null;

        if (gameStarted)
        {
            action = mode switch
            {
                LauncherSettings.LauncherVisibilities.Close => "close",
                LauncherSettings.LauncherVisibilities.HideClose => "hide",
                LauncherSettings.LauncherVisibilities.HideReopen => "hide",
                LauncherSettings.LauncherVisibilities.Minimize => "minimize",
                _ => null
            };
        }
        else
        {
            action = mode switch
            {
                LauncherSettings.LauncherVisibilities.HideClose => "close",
                LauncherSettings.LauncherVisibilities.HideReopen => "show",
                _ => null
            };
        }

        if (action is null)
        {
            return;
        }

        if (action == "close")
        {
            DetachGame();
        }

        Emit("window", new { action });
        Emit("log", new
        {
            level = "info",
            instance = (string?)null,
            text = action switch
            {
                "hide" => "启动器已隐藏（游戏退出后按设置处理）",
                "show" => "游戏退出，启动器已恢复",
                "minimize" => "启动器已最小化",
                _ => "启动器即将关闭（游戏继续运行，但不再监控日志）"
            }
        });
    }

    /// <summary>
    /// 放开对游戏进程的跟踪：宿主退出时不再取消 token，游戏就不会被连坐杀掉。
    /// </summary>
    private void DetachGame()
    {
        // 多开时"关闭启动器"意味着所有在跑的实例都交给系统：全部解绑，一个都不杀。
        lock (_runningGate)
        {
            foreach (CancellationTokenSource token in _running.Values)
            {
                token.Dispose();
            }

            _running.Clear();
        }
    }

    private object KillLaunch(JsonElement payload)
    {
        string name = Required(payload, "name");

        CancellationTokenSource? token;
        lock (_runningGate)
        {
            _running.TryGetValue(name, out token);
        }

        if (token is null)
        {
            return new { ok = false, error = $"实例「{name}」没有在运行。" };
        }

        try
        {
            token.Cancel();
            return new { ok = true };
        }
        catch (ObjectDisposedException)
        {
            return new { ok = false, error = $"实例「{name}」没有在运行。" };
        }
    }

    /// <summary>
    /// 观察上限。<c>ML_LAUNCH_TIMEOUT_SECONDS</c> 设了就在到点时连子树一起杀掉并归因 TimedOut。
    /// 默认不设——用户玩多久都行，这不是我们的业务。
    /// </summary>
    private static TimeSpan? LaunchTimeout()
    {
        string? raw = Environment.GetEnvironmentVariable("ML_LAUNCH_TIMEOUT_SECONDS");
        return int.TryParse(raw, out int seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    /// <summary>已下载的 log4j 配置路径；没有就是 null（用 JVM 内置默认，属正常用法）。</summary>
    private string? Log4jConfigPathFor(VersionMetadata version)
    {
        string? id = version.Logging?.Client?.File?.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        string path = _paths.LogConfig(id);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// 选一个 Java。设置里指定了就用指定的（并校验它够不够版本要求），否则自动挑。
    /// </summary>
    /// <remarks>
    /// 指定的 Java **版本不够时必须拒**，不能默默换一个能跑的：用户指定路径就是为了「这个版本配这个
    /// Java」，悄悄换掉会让别的整合包在别处炸，而那次崩溃现场跟这里的设置毫无关联，排查成本极高。
    /// </remarks>
    private JavaInstallation ResolveJava(VersionMetadata version)
    {
        int required = version.JavaVersion?.MajorVersion ?? 8;

        if (Settings.JavaHome is { Length: > 0 } pinnedHome)
        {
            var pinned = JavaLocator.Inspect(pinnedHome, JavaSource.User)
                ?? throw new InvalidOperationException(
                    $"设置里指定的 Java 位置无效：{pinnedHome}（这个目录下没有 bin\\java.exe）");

            if (pinned.Major < required)
            {
                throw new InvalidOperationException(
                    $"设置里指定的 Java 是 {pinned.Version.Raw}，而这个版本需要 Java {required}。"
                    + "改设置里的 Java 位置，或者清空它让启动器自动选。");
            }

            return pinned;
        }

        return JavaLocator.SelectBest(JavaLocator.Discover(), required, preferredOrder: Settings.JavaOrder)
            ?? throw new InvalidOperationException(
                $"没找到 Java {required}。装一个，或者在设置里指定 Java 位置。");
    }

    private object JavaList()
    {
        var order = Settings.JavaOrder;

        int Rank(string home)
        {
            int index = order.FindIndex(p => string.Equals(p, home, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? int.MaxValue : index;
        }

        // 用户排过序的在前（按 rank），没排过的按版本从高到低——Discover 已经排好了。
        var sorted = JavaLocator.Discover()
            .OrderBy(j => Rank(j.Home))
            .ToList();

        return new
        {
            ok = true,
            java = sorted.Select(j => new
            {
                version = j.Version.Raw,
                major = j.Major,
                arch = j.Architecture.ToString(),
                source = j.Source.ToString(),
                vendor = j.Vendor,
                home = j.Home
            }).ToArray()
        };
    }

    // ── 辅助 ─────────────────────────────────────────────────────────

    private static string Required(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(name, out var value)
            || value.GetString() is not { Length: > 0 } text)
        {
            throw new ArgumentException($"缺参数 {name}");
        }

        return text;
    }

    internal static EnvironmentProfile CurrentEnvironment() => new()
    {
        OsName = EnvironmentProfile.OsWindows,
        OsVersion = Environment.GetEnvironmentVariable("ML_OS_VERSION")
            ?? Environment.OSVersion.Version.ToString(),
        Arch = Environment.Is64BitOperatingSystem ? "x86_64" : "x86",
        Features = new Dictionary<string, bool>(0)
    };

    private void Emit(string name, object data)
    {
        string json = JsonSerializer.Serialize(new { @event = name, data }, Json);

        // 内存里留一份最近事件：崩的时候进程来不及写日志，这就是"最后几秒"。
        _crash.Log(json);

        try
        {
            _emit(json);
        }
        catch (Exception ex)
        {
            // 推送失败绝不能反过来把装机/启动打断：事件是给界面看的，不是流程的一部分。
            Debug.WriteLine($"[emit] {name} 投递失败：{ex.Message}");
        }
    }

    /// <summary>把宿主事件广播给插件。序列化失败也不能影响主流程。</summary>
    private void RaisePluginEvent(string name, object payload)
    {
        try
        {
            _plugins.RaiseEvent(name, JsonSerializer.Serialize(payload));
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException)
        {
            Debug.WriteLine($"[plugin-event] {name} 序列化失败：{ex.Message}");
        }
    }

    private void EmitError(Exception ex, string forEvent = "error")
    {
        Debug.WriteLine(ex);
        Emit(forEvent, new { ok = false, error = $"{ex.GetType().Name}: {ex.Message}" });
    }

    public async ValueTask DisposeAsync()
    {
        lock (_runningGate)
        {
            foreach (CancellationTokenSource token in _running.Values)
            {
                try
                {
                    token.Cancel();
                    token.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // 已经释放过了。
                }
            }

            _running.Clear();
        }

        _startGate.Dispose();

        // 关掉 Job 句柄：绑上来的进程会被系统一起清掉。
        // 正常退出走这里；崩溃时是操作系统替我们做同一件事。
        _processGuard.Dispose();

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }
}

/// <summary>同步投递的进度收集器：装机最后一条进度必须送到，否则进度条停在 99%。</summary>
internal sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
