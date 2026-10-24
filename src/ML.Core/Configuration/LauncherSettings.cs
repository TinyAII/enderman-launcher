using System.Text.Json;
using System.Text.Json.Serialization;

namespace ML.Core.Configuration;

/// <summary>
/// 启动器的全局设置。**只放「用户改过、要跨次保留」的东西**——和 <c>InstanceMetadata</c>
/// 的分工是：这里管「启动器怎么工作」，实例管「这个实例怎么玩」。
/// </summary>
/// <remarks>
/// 一律用 <see cref="Normalize"/> 收口：配置文件是人手能改的，也会被不同版本写，
/// 所以读进来的值必须能扛住 <c>memoryMb: -1</c>、<c>windowWidth: 0</c>、
/// <c>playerName: "   "</c> 这些情况。夹取放在这里而不是各个调用点，
/// 是因为「每个调用点都记得校验」这件事从来没成功过。
/// </remarks>
public sealed record LauncherSettings
{
    /// <summary>
    /// 老版本的单个游戏目录。**只读兼容**：新配置写的是 <see cref="GameRoots"/>，
    /// 这个字段留着是为了让「只有 gameRoot 的旧 config.json」还能直接读进来，
    /// <see cref="Normalize"/> 会把它迁成列表里的一条。
    /// </summary>
    public string? GameRoot { get; init; }

    /// <summary>配置里登记过的所有 Minecraft 目录。</summary>
    public IReadOnlyList<GameRootEntry> GameRoots { get; init; } = [];

    /// <summary>当前选中的目录 id。为空时取列表第一条。</summary>
    public string? ActiveGameRootId { get; init; }

    /// <summary>
    /// 诊断日志。开启后启动时额外写一段头信息：完整启动命令（含每个 classpath 条目）、
    /// Java 路径与版本、gameDir、内存与 GC 预设、相关环境变量。
    /// </summary>
    public bool DiagnosticLog { get; init; }

    /// <summary>
    /// 每个任务内部同时下几个文件。默认 64，上限 256。
    /// </summary>
    /// <remarks>
    /// 注意这只是"文件项并发"，不是"一个文件切 256 块"——
    /// MC 的资源是几千个小文件，切块对它们没有意义。
    /// </remarks>
    public int DownloadThreads { get; init; } = DefaultDownloadThreads;

    /// <summary>全局限速（MB/s）。0 表示不限。</summary>
    public int DownloadSpeedLimitMb { get; init; }

    /// <summary>同时跑几个下载任务（1/2/3/5/8/10）。</summary>
    public int DownloadParallelTasks { get; init; } = DefaultDownloadParallelTasks;

    /// <summary>
    /// 启动前快速检查文件是否齐全（存在 + 大小，不算哈希）。null = 没设置过，按开启处理。
    /// </summary>
    /// <remarks>
    /// 用可空布尔而不是 <c>bool = true</c>：`System.Text.Json` 对**缺字段**给的是 false，
    /// 和"用户主动关掉"无法区分。可空类型才有第三个状态。
    /// </remarks>
    public bool? CheckFilesBeforeLaunch { get; init; } = true;

    /// <summary>检查发现缺文件时自动补齐。null = 没设置过，按开启处理。</summary>
    public bool? AutoRepairMissing { get; init; } = true;

    /// <summary>
    /// 插件文件变了（重新编译/替换 dll）之后，提醒重启启动器。null = 没设置过，按开启处理。
    /// </summary>
    /// <remarks>
    /// 热加载能把新 dll 装进来，但旧实例的静态状态、后台线程不一定收干净。
    /// 开发时这个提醒很烦，交付给别人时它很有用——所以做成开关。
    /// </remarks>
    public bool? WarnRestartAfterPluginChange { get; init; } = true;

    /// <summary>启动后自动进的服务器（<c>ip</c> 或 <c>ip:port</c>）。null 表示不进。</summary>
    public string? AutoJoinServer { get; init; }

    /// <summary>离线模式玩家名。正版账号接进来后由账号取代。</summary>
    public string PlayerName { get; init; } = DefaultPlayerName;

    /// <summary>自定义时的堆内存（MB）。<see cref="UseCustomMemory"/> 为 false 时不用它。</summary>
    public int MemoryMb { get; init; } = DefaultMemoryMb;

    /// <summary>
    /// 是否用"自定义"内存。默认 false = 自动配置。
    /// </summary>
    /// <remarks>
    /// 故意不用 <c>AutoMemory = true</c> 这种正向布尔：源生成器反序列化缺字段时给 <c>false</c>，
    /// 正向布尔会让"老配置文件没这个键"变成"用户选了自定义"，与默认值相反。
    /// </remarks>
    public bool UseCustomMemory { get; init; }

    /// <summary>启动游戏前先做一次内存优化（收缩各进程工作集）。</summary>
    public bool MemoryOptimize { get; init; }

    public int WindowWidth { get; init; } = DefaultWindowWidth;

    public int WindowHeight { get; init; } = DefaultWindowHeight;

    /// <summary>指定的 Java 根目录。null 或空表示交给 <c>JavaLocator.SelectBest</c> 自动选。</summary>
    public string? JavaHome { get; init; }

    /// <summary>上次用的版本，用于下次默认选中。</summary>
    public string? LastVersionId { get; init; }

    /// <summary>指定要用的微软应用（客户端）ID。留空 = 不能用正版登录，只能离线。
    /// 只影响宿主层拿令牌，内核不读它（内核零网络）。</summary>
    public string? MicrosoftClientId { get; init; }

    /// <summary>
    /// 游戏启动后启动器窗口怎么处理。取值见 <see cref="LauncherVisibilityModes"/>。
    /// </summary>
    public string LauncherVisibility { get; init; } = LauncherVisibilities.Keep;

    /// <summary>游戏进程优先级：high / normal / low。</summary>
    public string GamePriority { get; init; } = GamePriorities.Normal;

    /// <summary>游戏窗口大小模式：default / fullscreen / launcher / custom / maximized。</summary>
    public string GameWindowMode { get; init; } = GameWindowModes.Default;

    /// <summary>追加的 JVM 参数（原样透传给 java）。</summary>
    public List<string> ExtraJvmArguments { get; init; } = [];

    /// <summary>
    /// Java 列表的优先顺序（Java 根目录）。排在前面的优先，用于列表显示与自动挑选。
    /// </summary>
    public List<string> JavaOrder { get; init; } = [];

    /// <summary>追加的游戏参数（拼在启动命令末尾）。</summary>
    public List<string> ExtraGameArguments { get; init; } = [];

    /// <summary>启动前执行的命令。在游戏目录里执行，失败只警告不阻止启动。</summary>
    public string? PreLaunchCommand { get; init; }

    /// <summary>GC 预设：default / g1 / zgc。会额外拼几条 -XX 参数。</summary>
    public string GcPreset { get; init; } = GcPresets.Default;

    /// <summary>把启动器和 Java 设成用高性能（独显）运行。</summary>
    public bool PreferHighPerformanceGpu { get; init; }

    /// <summary>
    /// 禁用「Java 18- 中文路径兼容」。false（默认）= 启用兼容。
    /// </summary>
    /// <remarks>
    /// PCL 靠一个外部 wrapper 程序解决 Java 18 以下在中文路径下起不来的问题；
    /// 我们不带那个 exe，改为在路径含非 ASCII 且 Java &lt; 18 时建一个纯 ASCII 的目录联接
    /// （junction）再启动——目的相同。（8.3 短路径试过，短名仍带中文，无效。）
    /// </remarks>
    public bool DisableLaunchWrapper { get; init; }


    public const string DefaultPlayerName = "Player";

    public static class GcPresets
    {
        public const string Default = "default";
        public const string G1Standard = "g1";
        public const string G1Tuned = "g1-tuned";
        public const string Zgc = "zgc";
        public const string ZgcGenerational = "zgc-generational";

        public static readonly IReadOnlyList<string> All =
            [Default, G1Standard, G1Tuned, Zgc, ZgcGenerational];

        public static string Normalize(string? raw) =>
            All.Contains(raw ?? string.Empty, StringComparer.Ordinal) ? raw! : Default;
    }

    public static class GamePriorities
    {
        public const string High = "high";
        public const string Normal = "normal";
        public const string Low = "low";

        public static readonly IReadOnlyList<string> All = [High, Normal, Low];

        public static string Normalize(string? raw) =>
            All.Contains(raw ?? string.Empty, StringComparer.Ordinal) ? raw! : Normal;
    }

    public static class GameWindowModes
    {
        /// <summary>不传 --width/--height，游戏用默认 854×480。</summary>
        public const string Default = "default";

        /// <summary>传 --fullscreen。</summary>
        public const string FullScreen = "fullscreen";

        /// <summary>和启动器窗口一样大。</summary>
        public const string Launcher = "launcher";

        /// <summary>用实例里配的宽高。</summary>
        public const string Custom = "custom";

        /// <summary>屏幕工作区大小（视觉上等于最大化）。</summary>
        public const string Maximized = "maximized";

        public static readonly IReadOnlyList<string> All = [Default, FullScreen, Launcher, Custom, Maximized];

        public static string Normalize(string? raw) =>
            All.Contains(raw ?? string.Empty, StringComparer.Ordinal) ? raw! : Default;
    }

    public static class LauncherVisibilities
    {
        /// <summary>什么都不做。</summary>
        public const string Keep = "keep";

        /// <summary>游戏起来后立刻关掉启动器（游戏继续跑，但不再被监控）。</summary>
        public const string Close = "close";

        /// <summary>游戏起来后隐藏，退出后关闭启动器。</summary>
        public const string HideClose = "hide-close";

        /// <summary>游戏起来后隐藏，退出后重新打开。</summary>
        public const string HideReopen = "hide-reopen";

        /// <summary>游戏起来后最小化。</summary>
        public const string Minimize = "minimize";

        public static readonly IReadOnlyList<string> All = [Keep, Close, HideClose, HideReopen, Minimize];

        public static string Normalize(string? raw) =>
            All.Contains(raw ?? string.Empty, StringComparer.Ordinal) ? raw! : Keep;
    }
    public const int DefaultMemoryMb = 2048;
    public const int DefaultWindowWidth = 854;
    public const int DefaultWindowHeight = 480;

    public const int MinMemoryMb = 512;
    public const int MaxMemoryMb = 65536;
    public const int DefaultDownloadThreads = 64;
    public const int DefaultDownloadParallelTasks = 1;
    public const int MaxDownloadThreads = 256;
    public const int MaxDownloadSpeedLimitMb = 10_000;

    public const int MinWindowSide = 320;
    public const int MaxWindowSide = 7680;
    public const int MaxPlayerNameLength = 16;

    /// <summary>当前生效的目录。列表为空时是 null。</summary>
    [JsonIgnore]
    public GameRootEntry? ActiveGameRoot
    {
        get
        {
            if (GameRoots.Count == 0)
            {
                return null;
            }

            return GameRoots.FirstOrDefault(root => root.Id == ActiveGameRootId) ?? GameRoots[0];
        }
    }

    [JsonIgnore]
    public bool HasGameRoot => ActiveGameRoot is not null;

    /// <summary>夹取到合法区间并清掉空白项。任何写入或读入路径都必须经过它。</summary>
    /// <remarks>
    /// <b>0 与负数一律当作「这个字段没被设置过」，落回默认值，而不是夹到最小值。</b>
    /// 原因是实测到的：<c>System.Text.Json</c> 的源生成器反序列化一个**缺字段**的 JSON 时，
    /// 会给出 <c>0</c> 而不是属性初始化器里的值（<c>new LauncherSettings().WindowWidth</c> 是 854，
    /// 但反序列化 <c>{"memoryMb":3072}</c> 之后 <c>WindowWidth</c> 是 0）。
    /// 手写一个只改一个字段的 config.json 是很正常的用法，那时候夹取会把窗口变成 320×320，
    /// 用户看到的是一个他从没设置过的值——所以「缺字段」必须落回默认值。
    /// </remarks>
    public LauncherSettings Normalize()
    {
        (List<GameRootEntry> roots, string? active) = NormalizeGameRoots(GameRoots, ActiveGameRootId, GameRoot);

        return this with
        {
            GameRoot = string.IsNullOrWhiteSpace(GameRoot) ? null : GameRoot!.Trim(),
            GameRoots = roots,
            ActiveGameRootId = active,
            PlayerName = NormalizePlayerName(PlayerName),
            MemoryMb = MemoryMb <= 0 ? DefaultMemoryMb : Math.Clamp(MemoryMb, MinMemoryMb, MaxMemoryMb),
            // 反序列化缺字段时给的是 0 而不是属性默认值，所以这三个也必须在这里兜底，
            // 否则旧配置读进来线程数就是 0（界面上滑块会卡在最左）。
            DownloadThreads = DownloadThreads <= 0
                ? DefaultDownloadThreads
                : Math.Clamp(DownloadThreads, 1, MaxDownloadThreads),
            DownloadSpeedLimitMb = DownloadSpeedLimitMb <= 0
                ? 0
                : Math.Min(DownloadSpeedLimitMb, MaxDownloadSpeedLimitMb),
            AutoJoinServer = string.IsNullOrWhiteSpace(AutoJoinServer) ? null : AutoJoinServer!.Trim(),
            DownloadParallelTasks = DownloadParallelTasks <= 0
                ? DefaultDownloadParallelTasks
                : Math.Clamp(DownloadParallelTasks, 1, 10),
            WindowWidth = WindowWidth <= 0 ? DefaultWindowWidth : Math.Clamp(WindowWidth, MinWindowSide, MaxWindowSide),
        WindowHeight = WindowHeight <= 0 ? DefaultWindowHeight : Math.Clamp(WindowHeight, MinWindowSide, MaxWindowSide),
        JavaHome = string.IsNullOrWhiteSpace(JavaHome) ? null : JavaHome!.Trim(),
        MicrosoftClientId = string.IsNullOrWhiteSpace(MicrosoftClientId) ? null : MicrosoftClientId!.Trim(),
        LastVersionId = string.IsNullOrWhiteSpace(LastVersionId) ? null : LastVersionId!.Trim(),
        LauncherVisibility = LauncherVisibilities.Normalize(LauncherVisibility),
        GamePriority = GamePriorities.Normalize(GamePriority),
        GameWindowMode = GameWindowModes.Normalize(GameWindowMode),
        ExtraJvmArguments = [.. (ExtraJvmArguments ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim())],
        ExtraGameArguments = [.. (ExtraGameArguments ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim())],
        JavaOrder = [.. (JavaOrder ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim())],
        PreLaunchCommand = string.IsNullOrWhiteSpace(PreLaunchCommand) ? null : PreLaunchCommand!.Trim(),
            GcPreset = GcPresets.Normalize(GcPreset),
        };
    }

    /// <summary>
    /// 整理目录列表：去空、去重、补 id，并保证选中的那一项真的存在。
    /// </summary>
    /// <remarks>
    /// 旧配置只有单个 <c>gameRoot</c>：列表为空时把它迁成第一条并选中。
    /// 迁移条件挂在「列表为空」上而不是版本号上——迁完列表就非空了，重复读一百次也只迁一次。
    /// </remarks>
    private static (List<GameRootEntry> Roots, string? Active) NormalizeGameRoots(
        IReadOnlyList<GameRootEntry>? raw,
        string? activeId,
        string? legacyGameRoot)
    {
        var roots = new List<GameRootEntry>();

        foreach (GameRootEntry entry in raw ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry?.Path))
            {
                continue;
            }

            GameRootEntry normalized;

            try
            {
                normalized = entry.Normalize();
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // 路径里有非法字符之类的，跳过这条，不要让整份配置读不出来
                continue;
            }

            if (roots.All(root => root.Id != normalized.Id))
            {
                roots.Add(normalized);
            }
        }

        if (roots.Count == 0 && !string.IsNullOrWhiteSpace(legacyGameRoot))
        {
            try
            {
                roots.Add(GameRootEntry.FromPath(legacyGameRoot));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // 老路径已经不能用了：列表留空，让用户重新添加
            }
        }

        string? active = null;

        if (roots.Count > 0)
        {
            active = roots.Any(root => root.Id == activeId) ? activeId : roots[0].Id;
        }

        return (roots, active);
    }

    /// <summary>
    /// 玩家名会被塞进命令行，所以只允许「不像参数、不像路径」的字符：
    /// 空白、控制字符、以及会被 JVM 当成选项的 <c>-</c> 开头一律不接受。
    /// </summary>
    public static string NormalizePlayerName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "Player";
        }

        string trimmed = raw.Trim();
        bool hasControl = trimmed.Any(char.IsControl);

        if (hasControl || trimmed.Any(char.IsWhiteSpace) || trimmed.StartsWith('-'))
        {
            return "Player";
        }

        return trimmed.Length > MaxPlayerNameLength ? trimmed[..MaxPlayerNameLength] : trimmed;
    }
}

/// <summary>
/// <see cref="LauncherSettings"/> 的序列化上下文。源生成而非反射，理由同
/// <c>InstanceJsonContext</c>：NativeAOT 下反射序列化会被裁掉。
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(LauncherSettings))]
internal sealed partial class LauncherSettingsJsonContext : JsonSerializerContext;
