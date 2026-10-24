namespace ML.Plugin.Abstractions;

/// <summary>
/// 插件入口。宿主用反射找到实现了这个接口的类型，创建实例、调 <see cref="InitializeAsync"/>。
/// </summary>
/// <remarks>
/// 这个程序集里**只允许出现 BCL 类型**。一旦出现 <c>ML.Core</c> 或 <c>ML.Host</c> 的类型，
/// 插件就被钉死在宿主的内部实现上：内核重构一次，所有插件跟着编译不过。
/// </remarks>
public interface IPlugin
{
    /// <summary>
    /// 初始化。宿主会给 10 秒，超时就当这个插件失败并禁用，不影响别的插件。
    /// </summary>
    Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken);

    /// <summary>卸载前调用。异常只记录，不阻断其它插件的卸载。</summary>
    Task ShutdownAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 启动前钩子。实现了它的插件会在装配启动命令之前被调用，可以往参数里追加东西。
/// </summary>
/// <remarks>
/// 和 <see cref="IPlugin"/> 分开是有意的：只想改参数的小插件不必写一套生命周期。
/// 宿主保证每次真实启动调用一次；只读操作（列实例、看日志）不会触发。
/// </remarks>
public interface ILaunchHook
{
    void OnBeforeLaunch(LaunchRequest request);
}

/// <summary>
/// 启动前拦截。和 <see cref="ILaunchHook"/> 是两件事：
/// 那个只能往参数里加东西，这个能**拒绝一次启动**。
/// </summary>
/// <remarks>
/// <para>
/// 典型用途：启动器级别的口令、内存不足时拦下、缺文件先补。
/// 拒绝后宿主不会起进程，界面上直接显示返回的原因。
/// </para>
/// <para>
/// 顺序上在 <see cref="ILaunchHook"/> 之后跑，也就是"参数都装配完了再问要不要放行"。
/// 多个插件都拒绝时，只报第一个的原因。
/// </para>
/// </remarks>
public interface ILaunchGuard
{
    /// <summary>返回 null（或 <see cref="HookResult.Pass"/>）放行；返回 <see cref="HookResult.Deny"/> 拒绝。</summary>
    HookResult? OnGuardLaunch(LaunchRequest request);
}

/// <summary>钩子结果。</summary>
public sealed record HookResult(bool Allow, string? Reason)
{
    public static HookResult Pass { get; } = new(true, null);

    public static HookResult Deny(string reason) => new(false, reason);
}

/// <summary>宿主交给插件的上下文。</summary>
public interface IPluginContext
{
    /// <summary>清单里的 id（反向域名），插件内唯一标识自己用。</summary>
    string Id { get; }

    /// <summary>插件所在目录的绝对路径。要读自带资源就基于它拼路径。</summary>
    string Directory { get; }

    /// <summary>
    /// 插件自己的可写目录（<c>数据目录/plugin-data/&lt;id&gt;</c>）。配置、缓存、下载临时文件放这里：
    /// 升级插件、停用重装都不会丢；写在插件目录里反而会在覆盖安装时被删。
    /// </summary>
    string DataDirectory { get; }

    /// <summary>
    /// 宿主状态与动作。插件不需要反射进宿主内部，从这里拿它该知道的：
    /// 有哪些实例、装了哪些版本、当前是谁的账号、能不能发起一次启动。
    /// </summary>
    IHostServices Host { get; }

    /// <summary>
    /// 插件自己的键值设置。存在 <c>plugin-data/&lt;id&gt;/settings.json</c>，
    /// 界面上的设置片段和插件代码读写的是同一份——不用各写一遍。
    /// </summary>
    IPluginSettings Settings { get; }

    IPluginLogger Log { get; }
}

/// <summary>插件设置：字符串键值。复杂结构自己 JSON 序列化后存。</summary>
public interface IPluginSettings
{
    string? Get(string key);

    void Set(string key, string? value);

    IReadOnlyDictionary<string, string?> All { get; }
}

/// <summary>
/// 插件日志。宿主统一加前缀和落盘，插件不要自己开文件——
/// 否则出问题的时候没人知道那行日志是谁写的。
/// </summary>
public interface IPluginLogger
{
    void Info(string message);

    void Warn(string message);

    // CA1716：Error 是 VB 的保留字。这个契约只给 .NET 插件用，
    // 而 Log.Error(...) 比 Log.LogError(...) 好读，所以保留命名并抑制这条。
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Naming", "CA1716:Identifiers should not match keywords",
        Justification = "插件契约只面向 .NET，短名字优先")]
    void Error(string message, Exception? exception = null);
}

/// <summary>
/// 一次启动的输入。插件可以往 <see cref="JvmArguments"/> / <see cref="GameArguments"/> 追加，
/// 但不能删改已有的——那些是启动器的判断，插件没有资格覆盖。
/// </summary>
public sealed class LaunchRequest
{
    private readonly List<string> _jvmArguments;
    private readonly List<string> _gameArguments;

    public LaunchRequest(
        string instanceName,
        string versionId,
        string playerName,
        string gameDirectory,
        IEnumerable<string> jvmArguments,
        IEnumerable<string> gameArguments)
    {
        InstanceName = instanceName;
        VersionId = versionId;
        PlayerName = playerName;
        GameDirectory = gameDirectory;
        _jvmArguments = [.. jvmArguments];
        _gameArguments = [.. gameArguments];
    }

    public string InstanceName { get; }

    public string VersionId { get; }

    public string PlayerName { get; }

    public string GameDirectory { get; }

    /// <summary>追加 JVM 参数（<c>-Xss</c>、<c>-Dxxx=y</c> 之类）。</summary>
    public IReadOnlyList<string> JvmArguments => _jvmArguments;

    /// <summary>追加游戏参数（在 mainClass 之后的那些）。</summary>
    public IReadOnlyList<string> GameArguments => _gameArguments;

    /// <summary>追加一条 JVM 参数。空串会被忽略——插件算出来是空的情况比想象中多。</summary>
    public void AddJvmArgument(string? argument)
    {
        if (!string.IsNullOrWhiteSpace(argument))
        {
            _jvmArguments.Add(argument);
        }
    }

    public void AddGameArgument(string? argument)
    {
        if (!string.IsNullOrWhiteSpace(argument))
        {
            _gameArguments.Add(argument);
        }
    }
}

/// <summary>
/// 宿主开放给插件的只读状态 + 一个动作（启动）。
/// </summary>
/// <remarks>
/// 这里是**契约**：方法一旦发布就不能随便改，只能加。所以刻意只放"下载器/市场真的需要"的东西，
/// 不放实例内部细节（配置读写、启动参数拼装）——那些是内核的判断，插件不该改。
/// </remarks>
public interface IHostServices
{
    /// <summary>游戏根目录（<c>.minecraft</c>）。</summary>
    string GameRoot { get; }

    /// <summary>当前所有实例。</summary>
    IReadOnlyList<HostInstance> Instances { get; }

    /// <summary>本机已安装的版本（只有 id 和类型，够界面展示即可）。</summary>
    IReadOnlyList<HostVersion> Versions { get; }

    /// <summary>最近使用的账号；一个都没有时为 null。</summary>
    HostAccount? CurrentAccount { get; }

    /// <summary>
    /// 启动一个实例。失败时返回原因，不抛异常——插件要能把失败原因显示给用户。
    /// </summary>
    Task<HostLaunchResult> LaunchAsync(string instanceName, CancellationToken cancellationToken = default);

    /// <summary>
    /// 版本安装能力。安装器插件用它把"本机没有的版本"装到磁盘上。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 放在契约里而不是让插件自己拼：插件程序集是隔离加载的，
    /// 它拿到的 <c>ML.Features</c> 和宿主那份不是同一个类型，直接调 <c>GameInstaller</c> 会炸。
    /// </para>
    /// <para>
    /// 性质是<b>便利层，不是唯一路径</b>：装版本的机制（下载、SHA1、natives 解压、legacy 摊平）
    /// 插件理论上都能自己写。宿主代劳是因为 natives 和 legacy 这两块是 Minecraft 特有的坑——
    /// 重写必错，而且报错离现场很远（到游戏启动才崩）。想自己做也完全可以，不要这份便利就行。
    /// </para>
    /// </remarks>
    IVersionInstallService Installer { get; }

    /// <summary>
    /// 下载队列。插件把要下的东西丢进来，就会出现在「下载」页：
    /// 用户能看到进度、能暂停、能限速。
    /// </summary>
    IDownloadService Downloads { get; }

    /// <summary>
    /// 把自己起的子进程交给宿主托管：**启动器一退（包括崩溃、被任务管理器结束），它跟着死**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 起进程、读输出、发命令、停止，插件自己用 BCL 就够了（<c>Process</c> 那套），
    /// 宿主不插手。这里只解决一件插件做不到的事：<b>宿主崩了之后谁去杀</b>。
    /// </para>
    /// <para>
    /// 实现方式是把进程绑到宿主的 Job Object（<c>KILL_ON_JOB_CLOSE</c>）：
    /// 宿主的句柄一没，整个进程树跟着消失，不留孤儿 java 占着端口。
    /// </para>
    /// <para>
    /// 返回 false 表示没托管成功（进程已退出 / 已经在别的 Job 里 / 平台不支持）。
    /// 托管失败不影响进程本身继续跑，只是失去了这条保证。
    /// </para>
    /// </remarks>
    bool AdoptProcess(int processId);

    /// <summary>
    /// 建议的服务器目录（<c>数据目录/servers</c>，已创建）。
    /// </summary>
    /// <remarks>
    /// **只是建议**：放不放这里插件自己定，宿主不检查、不接管、不扫描。
    /// 给这个路径是为了让不同插件的服务器默认能放在一起，便于用户找。
    /// </remarks>
    string ServerRoot { get; }
}

/// <summary>
/// 给插件用的下载队列。一个任务可以包含多个文件（"整合包"就该是一个任务）。
/// </summary>
public interface IDownloadService
{
    /// <summary>
    /// 提交一个下载任务。返回任务 id；队列满（500 个）时返回 null。
    /// </summary>
    string? Submit(string title, IReadOnlyList<HostDownloadFile> files);

    /// <summary>等任务结束（成功返回 true）。</summary>
    Task<bool> WaitAsync(string taskId, CancellationToken cancellationToken = default);

    /// <summary>查任务状态；找不到返回 null。</summary>
    HostDownloadStatus? Status(string taskId);
}

/// <summary>一个要下载的文件。目标路径是绝对路径，父目录会自动建。</summary>
public sealed record HostDownloadFile(string Url, string TargetPath, string? Sha1 = null, long? Size = null);

public sealed record HostDownloadStatus(
    string Id,
    string State,
    long TotalBytes,
    long DoneBytes,
    int FilesTotal,
    int FilesDone,
    string? Error);

/// <summary>
/// 装版本的能力：列远端版本 + 装其中一个。
/// </summary>
public interface IVersionInstallService
{
    /// <summary>Mojang 官方清单里的版本（新→旧）。</summary>
    Task<IReadOnlyList<HostRemoteVersion>> ListRemoteAsync(
        bool includeSnapshots,
        CancellationToken cancellationToken = default);

    /// <summary>把一个版本装到当前游戏目录。失败返回原因，不抛。</summary>
    Task<HostInstallResult> InstallAsync(
        string versionId,
        IProgress<HostInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record HostRemoteVersion(string Id, string Type, string? ReleaseTime);

public sealed record HostInstallResult(
    bool Ok,
    string VersionId,
    string? Error,
    int FilesDownloaded,
    int FilesSkipped,
    long Bytes);

public sealed record HostInstallProgress(string Stage, int Completed, int Total, long BytesDone);

/// <param name="Running">这个实例当前是否在跑。联机插件靠它决定"连哪个"。</param>
public sealed record HostInstance(
    string Name,
    string VersionId,
    string Directory,
    string GameDirectory,
    string ModsDirectory,
    string? AccountName,
    bool Running = false);

/// <param name="Protocol">
/// 网络协议号（比如 1.21.8 是 772），用来判断客户端和服务器对不对得上。
/// </param>
/// <remarks>
/// **目前恒为 null**：Mojang 的 version.json 里没有这个字段
/// （实测 1.21.8 的顶层只有 arguments / assetIndex / downloads / javaVersion 这些），
/// 协议号只出现在 ping 服务器的响应里。字段先留着，等有了数据源再填；
/// 插件现在要用，得自己维护"版本 → 协议号"的映射表。
/// </remarks>
public sealed record HostVersion(string Id, string? Type, int? Protocol = null);

public sealed record HostAccount(string Name, string Kind, string Uuid);

public sealed record HostLaunchResult(bool Ok, string? Error);

/// <summary>
/// 让界面上的按钮能调用插件自己的代码（<c>MLPlugin.call(name, payload)</c>）。
/// 没有它，插件的页面只能调宿主 API，碰不到插件自己的逻辑。
/// </summary>
public interface IPluginCommand
{
    /// <summary>返回值会原样回给页面；返回 null 表示没有内容。</summary>
    Task<string?> ExecuteAsync(string command, string? payload, CancellationToken cancellationToken);
}

/// <summary>
/// 宿主事件。目前四个：
/// <list type="bullet">
///   <item><c>instance.created</c> 新建实例</item>
///   <item><c>launch.preparing</c> 用户点了启动、正在装配（启动动画该在这一刻起）</item>
///   <item><c>launch.started</c> 进程真的起来了</item>
///   <item><c>launch.exited</c> 进程结束（含退出码）</item>
/// </list>
/// </summary>
public interface IEventHook
{
    void OnHostEvent(string name, string? payloadJson);
}
