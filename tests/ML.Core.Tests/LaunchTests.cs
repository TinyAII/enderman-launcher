using ML.Core.Install;
using ML.Core.Launch;
using ML.Core.Rules;
using ML.Core.Versioning;
using Xunit;

namespace ML.Core.Tests;

public class OfflineAccountTests
{
    [Fact]
    public void Notch_的离线_uuid_与社区通用值一致()
    {
        // 这个值不是从我们代码里抄回来的：它是 Mojang 之外所有启动器
        // 对 "OfflinePlayer:Notch" 取 MD5 再置 RFC 4122 v3 位的公认结果。
        Assert.Equal("b50ad385829d3141a2167e7d7539ba7f", OfflineAccount.ComputeUuid("Notch"));
    }

    [Fact]
    public void uuid_是_RFC4122_v3_形态()
    {
        string uuid = OfflineAccount.ComputeUuid("Steve");

        Assert.Equal(32, uuid.Length);
        Assert.All(uuid, c => Assert.True(char.IsAsciiHexDigitLower(c)));
        Assert.Equal('3', uuid[12]);
        Assert.Contains(uuid[16], "89ab");
    }

    [Fact]
    public void 不同名字给出不同_uuid()
    {
        Assert.NotEqual(OfflineAccount.ComputeUuid("Steve"), OfflineAccount.ComputeUuid("Alex"));
    }

    [Fact]
    public void Create_产出离线语义的账号()
    {
        var account = OfflineAccount.Create("Steve");

        Assert.Equal("Steve", account.PlayerName);
        Assert.Equal("0", account.AccessToken);
        Assert.Equal("legacy", account.UserType);
        Assert.True(account.IsOffline);
        Assert.Null(account.Xuid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Steve Jobs")]
    [InlineData("0123456789abcdefg")]
    public void 非法玩家名被拒绝(string name)
    {
        Assert.Throws<ArgumentException>(() => OfflineAccount.Create(name));
    }

    [Fact]
    public void 十六字符的名字允许()
    {
        var account = OfflineAccount.Create("0123456789abcdef");

        Assert.Equal(32, account.Uuid.Length);
    }
}

public class ClasspathBuilderTests
{
    private static readonly GamePaths Paths = new(@"C:\inst");

    private static LibraryEntry Library(string name, string? artifactPath = null, string? url = null, List<Rule>? rules = null)
    {
        var entry = new LibraryEntry
        {
            Name = name,
            Url = url,
            Rules = rules,
        };

        if (artifactPath is not null)
        {
            entry.Downloads = new LibraryDownloads
            {
                Artifact = new DownloadRef { Path = artifactPath, Url = "https://example.invalid/" + artifactPath },
            };
        }

        return entry;
    }

    [Fact]
    public void 真实_vanilla_的_classpath_不含_natives_分类器且以_client_jar_结尾()
    {
        var version = Fixtures.LoadVersion("vanilla-1.20.1.json");

        var plan = ClasspathBuilder.Build(Paths, version.Libraries, TestEnvironments.Windows, "1.20.1");

        Assert.DoesNotContain(plan.Entries, e => e.Contains("natives-windows", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Paths.ClientJar("1.20.1"), plan.Entries[^1]);
        Assert.Empty(plan.Warnings);

        // 条目总数受三条过滤影响（rules、natives 分类器、重复路径），
        // 所以只断言不变量：无重复、除 client.jar 外都在 libraries 下、关键库在位。
        Assert.Equal(plan.Entries.Count, plan.Entries.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(
            plan.Entries,
            e => e.EndsWith(@"org\lwjgl\lwjgl\3.3.1\lwjgl-3.3.1.jar", StringComparison.Ordinal));
        Assert.Contains(
            plan.Entries,
            e => e.EndsWith(@"com\mojang\authlib\4.0.43\authlib-4.0.43.jar", StringComparison.Ordinal));
        Assert.All(
            plan.Entries.Take(plan.Entries.Count - 1),
            e => Assert.StartsWith(Paths.Libraries + Path.DirectorySeparatorChar, e, StringComparison.Ordinal));
    }

    [Fact]
    public void natives_分类器条目被剔除()
    {
        var libraries = new[]
        {
            Library("org.lwjgl:lwjgl:3.3.1", @"org\lwjgl\lwjgl\3.3.1\lwjgl-3.3.1.jar"),
            Library("org.lwjgl:lwjgl:3.3.1:natives-windows", @"org\lwjgl\lwjgl\3.3.1\lwjgl-3.3.1-natives-windows.jar"),
        };

        var plan = ClasspathBuilder.Build(Paths, libraries, TestEnvironments.Windows, "1.20.1");

        Assert.Equal(2, plan.Entries.Count);
        Assert.Contains(plan.Entries, e => e.EndsWith(@"lwjgl-3.3.1.jar", StringComparison.Ordinal));
        Assert.Contains(plan.Entries, e => e.EndsWith(@"1.20.1.jar", StringComparison.Ordinal));
    }

    [Fact]
    public void 只有仓库前缀的库按坐标算路径()
    {
        // Fabric 的元数据就是这个形状：只给 url，不给 downloads。
        var libraries = new[]
        {
            Library(
                "net.fabricmc:fabric-loader:0.15.11",
                url: "https://maven.fabricmc.net/"),
        };

        var plan = ClasspathBuilder.Build(Paths, libraries, TestEnvironments.Windows, "fabric-loader-0.15.11-1.20.1");

        Assert.Contains(
            plan.Entries,
            e => e.EndsWith(@"net\fabricmc\fabric-loader\0.15.11\fabric-loader-0.15.11.jar", StringComparison.Ordinal));
    }

    [Fact]
    public void 规则不放行的库被剔除()
    {
        var macOnly = new List<Rule>
        {
            new()
            {
                Action = "allow",
                Os = new OsCondition { Name = "osx" },
            },
        };

        var libraries = new[] { Library("ca.weblite:java-objc-bridge:1.1", @"ca\weblite\x.jar", rules: macOnly) };

        var plan = ClasspathBuilder.Build(Paths, libraries, TestEnvironments.Windows, "1.20.1");

        Assert.Single(plan.Entries);
        Assert.Equal(Paths.ClientJar("1.20.1"), plan.Entries[0]);
    }

    [Fact]
    public void 重复路径只保留第一次出现()
    {
        var libraries = new[]
        {
            Library("g:a:1", @"g\a\1\a-1.jar"),
            Library("g:a:1", @"g\a\1\a-1.jar"),
            Library("g:b:1", @"g\a\1\a-1.jar"),
        };

        var plan = ClasspathBuilder.Build(Paths, libraries, TestEnvironments.Windows, "1.20.1");

        Assert.Equal(2, plan.Entries.Count);
    }

    [Fact]
    public void 无法解析的库坐标给警告而不是抛异常()
    {
        var libraries = new[] { Library("这不是坐标") };

        var plan = ClasspathBuilder.Build(Paths, libraries, TestEnvironments.Windows, "1.20.1");

        Assert.Single(plan.Entries);
        Assert.Single(plan.Warnings);
        Assert.Contains("这不是坐标", plan.Warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AsArgument_用平台分隔符拼接()
    {
        var plan = ClasspathBuilder.Build(Paths, [], TestEnvironments.Windows, "1.20.1");

        Assert.Equal(Paths.ClientJar("1.20.1"), plan.AsArgument);
    }

    private static int CountNatives(IEnumerable<LibraryEntry> libraries) =>
        libraries.Count(l => l.Name is not null
                             && l.Name.Contains(":natives-", StringComparison.OrdinalIgnoreCase));
}

public class LaunchCommandBuilderTests
{
    private static readonly GamePaths Paths = new(@"C:\inst");

    /// <summary>离线账号会让这两个参数的值为空串——这是合法取值，不是漏填。</summary>
    private static readonly string[] EmptyArgumentOwners = ["--clientId", "--xuid"];

    private static LaunchOptions Options(
        VersionMetadata version,
        LaunchAccount? account = null,
        (int, int)? windowSize = null,
        string? log4j = null) => new()
        {
            Paths = Paths,
            Version = version,
            Environment = TestEnvironments.Windows,
            JavaExecutable = @"C:\Java\bin\java.exe",
            GameDirectory = @"C:\inst",
            Account = account ?? OfflineAccount.Create("Steve"),
            LauncherName = "ML",
            LauncherVersion = "0.1.0",
            MaxMemoryMb = 2048,
            Log4jConfigPath = log4j,
            WindowSize = windowSize,
        };

    [Fact]
    public void 真实_vanilla_的参数全部占位符可解析()
    {
        var command = LaunchCommandBuilder.Build(Options(Fixtures.LoadVersion("vanilla-1.20.1.json")));

        Assert.True(
            command.UnresolvedPlaceholders.Count == 0,
            "未解析占位符：" + string.Join(", ", command.UnresolvedPlaceholders));
        Assert.True(command.Warnings.Count == 0, "警告：" + string.Join(" | ", command.Warnings));
        Assert.Equal(@"C:\Java\bin\java.exe", command.JavaExecutable);
        Assert.Equal(@"C:\inst", command.WorkingDirectory);
    }

    [Fact]
    public void 真实_vanilla_的关键参数齐全()
    {
        var command = LaunchCommandBuilder.Build(Options(Fixtures.LoadVersion("vanilla-1.20.1.json")));
        var args = command.Arguments;

        Assert.Equal("-Xmx2048m", args[0]);
        Assert.Contains("net.minecraft.client.main.Main", args);
        AssertContainsPair(args, "--username", "Steve");
        AssertContainsPair(args, "--version", "1.20.1");
        AssertContainsPair(args, "--gameDir", @"C:\inst");
        AssertContainsPair(args, "--assetsDir", @"C:\inst\assets");
        AssertContainsPair(args, "--assetIndex", "5");
        AssertContainsPair(args, "--userType", "legacy");
        AssertContainsPair(args, "--accessToken", "0");
        AssertContainsPair(args, "--uuid", OfflineAccount.ComputeUuid("Steve"));
        Assert.Contains(@"-Djava.library.path=C:\inst\versions\1.20.1\natives", args);
        Assert.Contains("-cp", args);

    }

    [Fact]
    public void classpath_参数就是_ClasspathPlan_拼出来的串()
    {
        var version = Fixtures.LoadVersion("vanilla-1.20.1.json");
        var command = LaunchCommandBuilder.Build(Options(version));

        int cpIndex = command.Arguments.ToList().IndexOf("-cp");
        Assert.True(cpIndex >= 0);
        Assert.Equal(command.Classpath.Count, command.Classpath.Count(e => e.Length > 0));
        Assert.Equal(string.Join(Path.PathSeparator, command.Classpath), command.Arguments[cpIndex + 1]);
    }

    [Fact]
    public void 没实现的可选参数整条丢掉()
    {
        // 不丢会变成 --width "" / --quickPlayPath ""，游戏解析时直接抛异常。
        var command = LaunchCommandBuilder.Build(Options(Fixtures.LoadVersion("vanilla-1.20.1.json")));

        Assert.DoesNotContain(command.Arguments, a => a.Contains("quickPlay", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("--width", command.Arguments);
        Assert.DoesNotContain("--height", command.Arguments);
        // --clientId / --xuid 对离线账号就是空串，这是合法取值；除它们之外不该出现空参数。
        for (int i = 0; i < command.Arguments.Count; i++)
        {
            if (command.Arguments[i].Length == 0)
            {
                Assert.True(i > 0, $"参数 {i} 是空的");
                Assert.Contains(command.Arguments[i - 1], EmptyArgumentOwners, StringComparer.Ordinal);
            }
        }
    }

    [Fact]
    public void 给了窗口尺寸就带上_width_height()
    {
        var command = LaunchCommandBuilder.Build(
            Options(Fixtures.LoadVersion("vanilla-1.20.1.json"), windowSize: (1280, 720)));

        AssertContainsPair(command.Arguments, "--width", "1280");
        AssertContainsPair(command.Arguments, "--height", "720");
    }

    [Fact]
    public void 给了_log4j_路径就替换掉_path_占位符()
    {
        string configPath = @"C:\inst\assets\log_configs\client-1.12.xml";
        var command = LaunchCommandBuilder.Build(
            Options(Fixtures.LoadVersion("vanilla-1.20.1.json"), log4j: configPath));

        Assert.Contains($"-Dlog4j.configurationFile={configPath}", command.Arguments);
        Assert.Empty(command.UnresolvedPlaceholders);
    }

    [Fact]
    public void 合并后的_forge_用_BootstrapLauncher_且库更多()
    {
        var vanilla = Fixtures.LoadVersion("vanilla-1.20.1.json");
        var forge = Fixtures.LoadVersion("forge-1.20.1-47.2.0.json");
        var merged = VersionMerger.Merge([vanilla, forge]);

        var vanillaCommand = LaunchCommandBuilder.Build(Options(vanilla));
        var forgeCommand = LaunchCommandBuilder.Build(Options(merged));

        Assert.Contains("cpw.mods.bootstraplauncher.BootstrapLauncher", forgeCommand.Arguments);
        Assert.Contains("net.minecraft.client.main.Main", vanillaCommand.Arguments);
        Assert.True(forgeCommand.Classpath.Count > vanillaCommand.Classpath.Count);
        Assert.Empty(forgeCommand.UnresolvedPlaceholders);
    }

    [Fact]
    public void 缺_mainClass_直接报错()
    {
        var version = Fixtures.LoadVersion("vanilla-1.20.1.json");
        version.MainClass = null;

        var error = Assert.Throws<InvalidOperationException>(() => LaunchCommandBuilder.Build(Options(version)));
        Assert.Contains("mainClass", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 缺_id_直接报错()
    {
        var version = Fixtures.LoadVersion("vanilla-1.20.1.json");
        version.Id = null;

        Assert.Throws<InvalidOperationException>(() => LaunchCommandBuilder.Build(Options(version)));
    }

    [Fact]
    public void 旧版元数据退回_minecraftArguments()
    {
        var version = new VersionMetadata
        {
            Id = "1.7.10",
            MainClass = "net.minecraft.client.main.Main",
            MinecraftArguments = "--username ${auth_player_name} --gameDir ${game_directory}",
            Libraries = [],
            Assets = "legacy",
        };

        var command = LaunchCommandBuilder.Build(Options(version));

        AssertContainsPair(command.Arguments, "--username", "Steve");
        AssertContainsPair(command.Arguments, "--gameDir", @"C:\inst");
        Assert.Equal(@"C:\inst\versions\1.7.10\natives", command.Arguments[1]["-Djava.library.path=".Length..]);
        Assert.Empty(command.UnresolvedPlaceholders);
    }

    [Fact]
    public void Describe_把含空格的参数加引号()
    {
        var command = new LaunchCommand
        {
            JavaExecutable = @"C:\Java\bin\java.exe",
            Arguments = ["-Dx=1", @"C:\Program Files\a.jar"],
            WorkingDirectory = @"C:\inst",
            Classpath = [],
        };

        string text = command.Describe();

        Assert.Contains("\"C:\\Program Files\\a.jar\"", text, StringComparison.Ordinal);
        Assert.StartsWith(@"C:\Java\bin\java.exe -Dx=1", text, StringComparison.Ordinal);
    }

    private static void AssertContainsPair(IReadOnlyList<string> arguments, string flag, string value)
    {
        for (int i = 0; i < arguments.Count - 1; i++)
        {
            if (arguments[i] == flag && arguments[i + 1] == value)
            {
                return;
            }
        }

        Assert.Fail($"参数里找不到 {flag} {value}");
    }
}

public class GameProcessRunnerTests
{
    private static string Shell => Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";

    private static LaunchCommand Command(params string[] arguments) => new()
    {
        JavaExecutable = Shell,
        Arguments = arguments,
        WorkingDirectory = Path.GetTempPath(),
        Classpath = [],
    };

    [Fact]
    public async Task 退出码零算正常退出()
    {
        var outcome = await GameProcessRunner.RunAsync(Command("/c", "exit 0"), TimeSpan.FromSeconds(20));

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(LaunchDiagnosis.CleanExit, outcome.Diagnosis);
        Assert.False(outcome.TimedOut);
    }

    [Fact]
    public async Task 非零退出且无特征算崩溃()
    {
        var outcome = await GameProcessRunner.RunAsync(Command("/c", "exit 3"), TimeSpan.FromSeconds(20));

        Assert.Equal(3, outcome.ExitCode);
        Assert.Equal(LaunchDiagnosis.Crashed, outcome.Diagnosis);
    }

    [Fact]
    public async Task 见到_Exception_in_thread_算崩溃()
    {
        var outcome = await GameProcessRunner.RunAsync(
            Command("/c", "echo Exception in thread \"main\" java.lang.RuntimeException & exit 1"),
            TimeSpan.FromSeconds(20));

        Assert.Equal(LaunchDiagnosis.Crashed, outcome.Diagnosis);
        Assert.Contains(outcome.TailOutput, l => l.Contains("Exception in thread", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 见到_UnsupportedClassVersionError_算_Java_起不来()
    {
        var outcome = await GameProcessRunner.RunAsync(
            Command("/c", "echo java.lang.UnsupportedClassVersionError: class file version 65.0 & exit 1"),
            TimeSpan.FromSeconds(20));

        Assert.Equal(LaunchDiagnosis.JavaStartupFailure, outcome.Diagnosis);
    }

    [Fact]
    public async Task 超时被杀且标记_TimedOut()
    {
        var outcome = await GameProcessRunner.RunAsync(
            Command("/c", "ping -n 8 127.0.0.1 >nul"),
            TimeSpan.FromMilliseconds(400));

        Assert.True(outcome.TimedOut);
        Assert.Equal(LaunchDiagnosis.TimedOut, outcome.Diagnosis);
        Assert.True(outcome.Duration < TimeSpan.FromSeconds(10), $"用时 {outcome.Duration}");
    }

    [Fact]
    public async Task 外部取消算_Killed()
    {
        using var cancellation = new CancellationTokenSource(400);

        var outcome = await GameProcessRunner.RunAsync(
            Command("/c", "ping -n 8 127.0.0.1 >nul"),
            TimeSpan.FromSeconds(30),
            cancellationToken: cancellation.Token);

        Assert.Equal(LaunchDiagnosis.Killed, outcome.Diagnosis);
        Assert.False(outcome.TimedOut);
    }

    [Fact]
    public async Task 输出被收进_HeadOutput()
    {
        var outcome = await GameProcessRunner.RunAsync(
            Command("/c", "echo first-line & echo second-line"),
            TimeSpan.FromSeconds(20));

        Assert.Contains(outcome.HeadOutput, l => l.Contains("first-line", StringComparison.Ordinal));
        Assert.Contains(outcome.HeadOutput, l => l.Contains("second-line", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 见到游戏初始化特征就置位()
    {
        var outcome = await GameProcessRunner.RunAsync(
            Command("/c", "echo Backend library: LWJGL 3 & exit 0"),
            TimeSpan.FromSeconds(20));

        Assert.True(outcome.SawStartupMarker);
    }

    [Fact]
    public async Task 每行都回调给调用方()
    {
        var lines = new List<string>();

        await GameProcessRunner.RunAsync(
            Command("/c", "echo alpha & echo beta"),
            TimeSpan.FromSeconds(20),
            line => { lock (lines) { lines.Add(line); } });

        Assert.Contains(lines, l => l.Trim() == "alpha");
        Assert.Contains(lines, l => l.Trim() == "beta");
    }
}
