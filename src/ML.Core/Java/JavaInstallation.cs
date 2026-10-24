namespace ML.Core.Java;

/// <summary>JVM 位数。必须和游戏要加载的 natives 对齐，32 位 JVM 读不了 64 位 dll。</summary>
public enum JavaArchitecture
{
    Unknown = 0,
    X86,
    X64,
    Arm64
}

/// <summary>发现来源。枚举值就是优先级，越小越可信。</summary>
public enum JavaSource
{
    /// <summary>用户显式指定。</summary>
    User = 0,

    /// <summary><c>JAVA_HOME</c> 环境变量。</summary>
    JavaHome = 1,

    /// <summary>PATH 里的 java.exe。</summary>
    Path = 2,

    /// <summary>Windows 注册表 JavaSoft。</summary>
    Registry = 3,

    /// <summary>厂商默认安装目录。</summary>
    WellKnownDirectory = 4,

    /// <summary>Mojang 随游戏下发的运行时（<c>.minecraft\runtime</c>）。</summary>
    MojangRuntime = 5
}

/// <summary>一个可用的 Java 安装。</summary>
public sealed record JavaInstallation
{
    /// <summary>根目录的绝对路径，无尾分隔符。</summary>
    public required string Home { get; init; }

    /// <summary><c>bin\java.exe</c> 的绝对路径。</summary>
    public required string ExecutablePath { get; init; }

    public required JavaVersion Version { get; init; }

    public JavaArchitecture Architecture { get; init; } = JavaArchitecture.Unknown;

    /// <summary>厂商，如 <c>Eclipse Adoptium</c>、<c>Azul Systems, Inc.</c>。可能为空。</summary>
    public string? Vendor { get; init; }

    public JavaSource Source { get; init; } = JavaSource.User;

    /// <summary>是否有可信的 <c>release</c> 文件。没有时版本靠启动进程探得，可信度低一档。</summary>
    public bool HasReleaseFile { get; init; }

    public int Major => Version.Major;

    public override string ToString() =>
        $"{Version.Raw} ({(Architecture == JavaArchitecture.Unknown ? "?" : Architecture.ToString())}) @ {Home}";
}
