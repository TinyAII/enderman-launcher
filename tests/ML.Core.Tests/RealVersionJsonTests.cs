using System.Text.Json;
using ML.Core.Launch;
using ML.Core.Maven;
using ML.Core.Rules;
using ML.Core.Versioning;

namespace ML.Core.Tests;

/// <summary>
/// 真机 fixture 测试：这里用的不是手写 JSON，而是真实的
/// Mojang 1.20.1 / Forge 47.2.0 / Fabric 0.15.11 的 version.json。
/// 手写样本只能证明「我按自己的理解写了代码」，真文件才能证明「理解是对的」。
/// </summary>
public class RealVersionJsonTests
{
    private const string VanillaFile = "vanilla-1.20.1.json";
    private const string ForgeFile = "forge-1.20.1-47.2.0.json";
    private const string FabricFile = "fabric-1.20.1.json";

    private static readonly string[] AllFiles = [VanillaFile, ForgeFile, FabricFile];

    // ── 解析层 ────────────────────────────────────────────────────

    [Fact]
    public void 三个真实文件都能解析()
    {
        foreach (var file in AllFiles)
        {
            var version = Fixtures.LoadVersion(file);
            Assert.False(string.IsNullOrEmpty(version.Id));
        }
    }

    [Fact]
    public void vanilla_1_20_1_关键字段()
    {
        var vanilla = Fixtures.LoadVersion(VanillaFile);

        Assert.Equal("1.20.1", vanilla.Id);
        Assert.Null(vanilla.InheritsFrom);
        Assert.Equal("net.minecraft.client.main.Main", vanilla.MainClass);
        Assert.Equal("5", vanilla.Assets);
        Assert.Equal("5", vanilla.AssetIndex!.Id);
        Assert.Equal("release", vanilla.Type);
        Assert.Equal(17, vanilla.JavaVersion!.MajorVersion);
        Assert.Equal("java-runtime-gamma", vanilla.JavaVersion.Component);

        Assert.Equal(88, vanilla.Libraries!.Count);
        Assert.Equal(28, vanilla.Arguments!.Game!.Count);
        Assert.Equal(11, vanilla.Arguments.Jvm!.Count);

        // 1.20.1 用的已经是结构化 arguments，不是老的 minecraftArguments
        Assert.Null(vanilla.MinecraftArguments);
    }

    [Fact]
    public void 所有真实库坐标都能解析成_Maven_坐标()
    {
        // 这条是整个解析层最有价值的不变量：
        // 只要有一个 name 解析不出来，下载阶段就会静默跳过那个库，最后以
        // 「NoClassDefFoundError」的形式在游戏里爆掉——离现场十万八千里。
        foreach (var file in AllFiles)
        {
            var version = Fixtures.LoadVersion(file);
            var failures = version.Libraries!
                .Where(lib => !MavenCoordinate.TryParse(lib.Name, out _))
                .Select(lib => lib.Name)
                .ToArray();

            Assert.True(
                failures.Length == 0,
                $"{file} 有 {failures.Length} 条坐标解析失败: {string.Join(", ", failures)}");
        }
    }

    [Fact]
    public void fabric_的库用_url_前缀而不是_downloads()
    {
        var fabric = Fixtures.LoadVersion(FabricFile);

        Assert.Equal(8, fabric.Libraries!.Count);
        Assert.All(fabric.Libraries, lib =>
        {
            Assert.False(string.IsNullOrEmpty(lib.Url));
            Assert.Null(lib.Downloads);
        });

        // fabric 的库也在 maven.fabricmc.net 上，不是 Mojang 的 libraries
        Assert.All(fabric.Libraries, lib =>
            Assert.StartsWith("https://maven.fabricmc.net/", lib.Url, StringComparison.Ordinal));
    }

    [Fact]
    public void 带_rules_的库能被正确读到()
    {
        var vanilla = Fixtures.LoadVersion(VanillaFile);

        var osxOnly = vanilla.Libraries!
            .Where(lib => lib.Rules is { Count: > 0 })
            .ToArray();

        // 1.20.1 里有一批只对特定平台生效的库（objc-bridge、epoll 等）
        Assert.NotEmpty(osxOnly);
        Assert.All(osxOnly, lib => Assert.NotNull(lib.Rules![0].Os));

        // 在 Windows 上，只给 osx 的那条必须被判为不放行
        var env = TestEnvironments.Windows;
        var objcBridge = vanilla.Libraries!
            .Single(lib => lib.Name == "ca.weblite:java-objc-bridge:1.1");

        Assert.False(RuleEvaluator.IsAllowed(objcBridge.Rules, env));
    }

    [Fact]
    public void 时间字段用宽松解析而不是严格反序列化()
    {
        // 这条是真实文件逼出来的坑：
        //   Mojang / Forge  -> "2023-06-12T13:25:51+00:00"
        //   Fabric          -> "2026-10-20T09:54:00+0000"   ← 没有冒号
        // 用 DateTimeOffset 严格反序列化时 Fabric 那份会让整个 version.json 解析失败。
        var vanilla = Fixtures.LoadVersion(VanillaFile);
        var fabric = Fixtures.LoadVersion(FabricFile);

        Assert.True(VersionTimes.TryParse(vanilla.ReleaseTime, out var vanillaTime));
        Assert.Equal(new DateTimeOffset(2023, 6, 12, 13, 25, 51, TimeSpan.Zero), vanillaTime);

        Assert.True(VersionTimes.TryParse(fabric.ReleaseTime, out var fabricTime));
        Assert.Equal(TimeSpan.Zero, fabricTime.Offset);
        Assert.Equal("2026-10-20T09:54:00+0000", fabric.ReleaseTime);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a date")]
    public void 坏时间字段返回false而不是抛出(string? raw)
    {
        Assert.False(VersionTimes.TryParse(raw, out _));
        Assert.Null(VersionTimes.ParseOrNull(raw));
    }

    // ── 合并层 ────────────────────────────────────────────────────

    [Fact]
    public void forge_继承链合并后字段来源正确()
    {
        var vanilla = Fixtures.LoadVersion(VanillaFile);
        var forge = Fixtures.LoadVersion(ForgeFile);
        var merged = VersionMerger.Merge([vanilla, forge]);

        Assert.Equal("1.20.1-forge-47.2.0", merged.Id);
        Assert.Equal("cpw.mods.bootstraplauncher.BootstrapLauncher", merged.MainClass);
        Assert.Null(merged.InheritsFrom);

        // Forge 的 JSON 里没有这些字段，必须从 vanilla 继承下来
        Assert.Equal("5", merged.Assets);
        Assert.Equal("5", merged.AssetIndex!.Id);
        Assert.Equal(17, merged.JavaVersion!.MajorVersion);
    }

    [Fact]
    public void forge_继承链合并后库数量是_117()
    {
        var vanilla = Fixtures.LoadVersion(VanillaFile);
        var forge = Fixtures.LoadVersion(ForgeFile);
        var merged = VersionMerger.Merge([vanilla, forge]);

        // 29 + 88 = 117，零重叠。
        // 乍看「应该有重叠」，实测没有：Forge 用的 asm 是 9.5，vanilla 用的是更早的版本，
        // 不同版本号 = 不同 Maven 坐标 = 两条都留。这正是继承链格式的坑，
        // 也是为什么去重必须按完整坐标而不是 groupId:artifactId。
        Assert.Equal(117, merged.Libraries!.Count);

        var keys = merged.Libraries
            .Select(lib =>
            {
                Assert.True(MavenCoordinate.TryParse(lib.Name, out var c), lib.Name);
                return c.Key;
            })
            .ToArray();

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void forge_继承链合并后两边的库都在()
    {
        var merged = VersionMerger.Merge(
            [Fixtures.LoadVersion(VanillaFile), Fixtures.LoadVersion(ForgeFile)]);

        var names = merged.Libraries!.Select(lib => lib.Name).ToHashSet(StringComparer.Ordinal);

        // Forge 自有
        Assert.Contains("cpw.mods:securejarhandler:2.1.10", names);
        // vanilla 自有
        Assert.Contains("com.google.code.gson:gson:2.10", names);
        Assert.Contains("com.mojang:authlib:4.0.43", names);
    }

    [Fact]
    public void forge_继承链合并后参数是两边拼接()
    {
        var vanilla = Fixtures.LoadVersion(VanillaFile);
        var forge = Fixtures.LoadVersion(ForgeFile);
        var merged = VersionMerger.Merge([vanilla, forge]);

        Assert.Equal(
            vanilla.Arguments!.Game!.Count + forge.Arguments!.Game!.Count,
            merged.Arguments!.Game!.Count);

        Assert.Equal(
            vanilla.Arguments.Jvm!.Count + forge.Arguments.Jvm!.Count,
            merged.Arguments.Jvm!.Count);
    }

    // ── 参数展开层 ────────────────────────────────────────────────

    [Fact]
    public void 真实参数在完整占位符下展开后没有残留()
    {
        var merged = VersionMerger.Merge(
            [Fixtures.LoadVersion(VanillaFile), Fixtures.LoadVersion(ForgeFile)]);

        var env = TestEnvironments.Windows;
        var placeholders = TestEnvironments.FullPlaceholders();

        var jvm = ArgumentExpander.Expand(merged.Arguments!.Jvm, env, placeholders);
        var game = ArgumentExpander.Expand(merged.Arguments.Game, env, placeholders);

        Assert.True(
            jvm.IsClean,
            "jvm 参数有未解析占位符: " + string.Join(", ", jvm.UnresolvedPlaceholders));
        Assert.True(
            game.IsClean,
            "game 参数有未解析占位符: " + string.Join(", ", game.UnresolvedPlaceholders));

        Assert.All(jvm.Arguments, a =>
            Assert.DoesNotContain("${", a, StringComparison.Ordinal));
        Assert.All(game.Arguments, a =>
            Assert.DoesNotContain("${", a, StringComparison.Ordinal));
    }

    [Fact]
    public void 真实参数展开后能拿到关键启动项()
    {
        var merged = VersionMerger.Merge(
            [Fixtures.LoadVersion(VanillaFile), Fixtures.LoadVersion(ForgeFile)]);

        var env = TestEnvironments.Windows;
        var placeholders = TestEnvironments.FullPlaceholders();

        var game = ArgumentExpander.Expand(merged.Arguments!.Game, env, placeholders)
            .Arguments.ToList();
        var jvm = ArgumentExpander.Expand(merged.Arguments.Jvm, env, placeholders)
            .Arguments.ToList();

        int userIdx = game.IndexOf("--username");
        Assert.True(userIdx >= 0, "game 参数里没有 --username");
        Assert.Equal("Steve", game[userIdx + 1]);

        Assert.Contains("--version", game);
        Assert.Contains("--gameDir", game);
        Assert.Contains("--assetsDir", game);

        Assert.Contains(jvm, a => a.StartsWith("-Djava.library.path=", StringComparison.Ordinal));
        Assert.Contains(jvm, a => a.Contains("${", StringComparison.Ordinal) == false && a == "-cp");
    }

    [Fact]
    public void 不提供占位符时错误会被上报而不是静默吞掉()
    {
        var vanilla = Fixtures.LoadVersion(VanillaFile);

        var result = ArgumentExpander.Expand(
            vanilla.Arguments!.Jvm,
            TestEnvironments.Windows,
            new Dictionary<string, string>());

        Assert.False(result.IsClean);
        Assert.NotEmpty(result.UnresolvedPlaceholders);
    }

    [Fact]
    public void 平台专属参数在_Windows_上被剔除()
    {
        var vanilla = Fixtures.LoadVersion(VanillaFile);

        var jvm = ArgumentExpander.Expand(
            vanilla.Arguments!.Jvm,
            TestEnvironments.Windows,
            TestEnvironments.FullPlaceholders()).Arguments;

        // -XstartOnFirstThread 只对 macOS 有意义
        Assert.DoesNotContain("-XstartOnFirstThread", jvm);
    }

    /// <summary>
    /// 变更探测器：真实文件里出现的占位符集合必须与这份清单一致。
    /// Mojang 或 Forge 哪天加了新的占位符，这条测试会先炸，而不是等玩家启动失败。
    /// </summary>
    [Fact]
    public void 真实文件里的占位符集合没有变化()
    {
        string[] expected =
        [
            "assets_index_name", "assets_root", "auth_access_token", "auth_player_name",
            "auth_uuid", "auth_xuid", "classpath", "classpath_separator", "clientid",
            "game_directory", "launcher_name", "launcher_version", "library_directory",
            "natives_directory", "path", "quickPlayMultiplayer", "quickPlayPath",
            "quickPlayRealms", "quickPlaySingleplayer", "resolution_height",
            "resolution_width", "user_type", "version_name", "version_type"
        ];

        var actual = AllFiles
            .Select(Fixtures.ReadText)
            .SelectMany(text => System.Text.RegularExpressions.Regex
                .Matches(text, @"\$\{([A-Za-z0-9_]+)\}")
                .Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected.OrderBy(k => k, StringComparer.Ordinal).ToArray(), actual);
    }
}

internal static class TestEnvironments
{
    public static EnvironmentProfile Windows => new()
    {
        OsName = EnvironmentProfile.OsWindows,
        OsVersion = "10.0.26200",
        Arch = "x86_64",
        Features = new Dictionary<string, bool>(0)
    };

    /// <summary>覆盖真实文件里出现的全部 24 个占位符。</summary>
    public static Dictionary<string, string> FullPlaceholders() => new()
    {
        ["assets_index_name"] = "5",
        ["assets_root"] = @"C:\inst\assets",
        ["auth_access_token"] = "token",
        ["auth_player_name"] = "Steve",
        ["auth_uuid"] = "00000000000000000000000000000000",
        ["auth_xuid"] = "0",
        ["classpath"] = @"C:\inst\libraries\a.jar;C:\inst\versions\1.20.1\client.jar",
        ["classpath_separator"] = ";",
        ["clientid"] = "ml",
        ["game_directory"] = @"C:\inst",
        ["launcher_name"] = "ML",
        ["launcher_version"] = "0.1.0",
        ["library_directory"] = @"C:\inst\libraries",
        ["natives_directory"] = @"C:\inst\versions\1.20.1\natives",
        ["path"] = @"C:\inst\assets\log_configs\client-1.12.xml",
        ["quickPlayMultiplayer"] = "",
        ["quickPlayPath"] = "",
        ["quickPlayRealms"] = "",
        ["quickPlaySingleplayer"] = "",
        ["resolution_height"] = "480",
        ["resolution_width"] = "854",
        ["user_type"] = "msa",
        ["version_name"] = "1.20.1-forge-47.2.0",
        ["version_type"] = "release"
    };
}

internal static class Fixtures
{
    public static readonly Lazy<string> Directory = new(Resolve);

    public static string ReadText(string name) =>
        File.ReadAllText(Path.Combine(Directory.Value, name));

    public static VersionMetadata LoadVersion(string name) =>
        JsonSerializer.Deserialize<VersionMetadata>(ReadText(name))
        ?? throw new InvalidOperationException($"{name} 解析结果为空");

    private static string Resolve()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ml.slnx")))
            {
                return Path.Combine(dir.FullName, "tests", "fixtures");
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"从 {AppContext.BaseDirectory} 向上找不到 ml.slnx，无法定位 fixtures");
    }
}
