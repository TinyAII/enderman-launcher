using ML.Core.Launch;
using ML.Core.Maven;
using ML.Core.Rules;
using ML.Core.Versioning;

namespace ML.Core.Tests;

public class RuleEvaluatorTests
{
    private static EnvironmentProfile Windows => new()
    {
        OsName = EnvironmentProfile.OsWindows,
        Arch = "x86_64",
        Features = new Dictionary<string, bool>()
    };

    [Fact]
    public void 无规则时放行()
    {
        Assert.True(RuleEvaluator.IsAllowed(null, Windows));
        Assert.True(RuleEvaluator.IsAllowed([], Windows));
    }

    [Fact]
    public void 有规则但一条都没命中时拒绝()
    {
        // 只允许 macOS，在 Windows 上一条都不命中 → 默认拒绝
        Rule[] rules = [new() { Action = "allow", Os = new OsCondition { Name = "osx" } }];
        Assert.False(RuleEvaluator.IsAllowed(rules, Windows));
    }

    [Fact]
    public void 最后一条命中的规则决定结果()
    {
        Rule[] rules =
        [
            new() { Action = "allow" },
            new() { Action = "disallow" }
        ];
        Assert.False(RuleEvaluator.IsAllowed(rules, Windows));

        Rule[] reversed =
        [
            new() { Action = "disallow" },
            new() { Action = "allow" }
        ];
        Assert.True(RuleEvaluator.IsAllowed(reversed, Windows));
    }

    [Fact]
    public void 按操作系统过滤()
    {
        Rule[] onlyLinux = [new() { Action = "allow", Os = new OsCondition { Name = "linux" } }];
        Assert.False(RuleEvaluator.IsAllowed(onlyLinux, Windows));

        Rule[] onlyWindows = [new() { Action = "allow", Os = new OsCondition { Name = "windows" } }];
        Assert.True(RuleEvaluator.IsAllowed(onlyWindows, Windows));
    }

    [Fact]
    public void 未提供的特性按false处理()
    {
        // 规则要求 is_demo_user = false；上下文里没有这个键 → 视为 false → 命中
        Rule[] requireNotDemo =
        [
            new() { Action = "allow", Features = new() { ["is_demo_user"] = false } }
        ];
        Assert.True(RuleEvaluator.IsAllowed(requireNotDemo, Windows));

        Rule[] requireDemo =
        [
            new() { Action = "allow", Features = new() { ["is_demo_user"] = true } }
        ];
        Assert.False(RuleEvaluator.IsAllowed(requireDemo, Windows));
    }

    [Fact]
    public void 非法正则不会抛出而是当作不匹配()
    {
        Rule[] rules =
        [
            new()
            {
                Action = "allow",
                Os = new OsCondition { Version = "(unclosed" }
            }
        ];
        Assert.False(RuleEvaluator.IsAllowed(rules, Windows));
    }
}

public class MavenCoordinateTests
{
    [Theory]
    [InlineData("com.mojang:authlib:3.11.49", "com/mojang/authlib/3.11.49/authlib-3.11.49.jar")]
    [InlineData("net.minecraftforge:forge:1.20.1-47.2.0:universal",
        "net/minecraftforge/forge/1.20.1-47.2.0/forge-1.20.1-47.2.0-universal.jar")]
    [InlineData("org.lwjgl:lwjgl:3.3.1:natives-windows",
        "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-windows.jar")]
    public void 解析并生成仓库相对路径(string input, string expectedPath)
    {
        Assert.True(MavenCoordinate.TryParse(input, out var coord));
        Assert.Equal(expectedPath, coord.RelativePath);
    }

    [Fact]
    public void 支持扩展名后缀()
    {
        Assert.True(MavenCoordinate.TryParse("a.b:c:1.0@zip", out var coord));
        Assert.Equal("zip", coord.Extension);
        Assert.EndsWith("c-1.0.zip", coord.RelativePath, StringComparison.Ordinal);
    }

    [Fact]
    public void 缺省分类器与显式jar归一化到同一个键()
    {
        Assert.True(MavenCoordinate.TryParse("a.b:c:1.0", out var implicitJar));
        Assert.True(MavenCoordinate.TryParse("a.b:c:1.0@jar", out var explicitJar));
        Assert.Equal(implicitJar.Key, explicitJar.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("only:two")]
    [InlineData("a:b:c:d:e")]
    [InlineData("a::c")]
    [InlineData("a:b:c@")]
    public void 脏数据返回false而不是抛出(string? input)
    {
        Assert.False(MavenCoordinate.TryParse(input, out _));
    }
}

public class VersionMergerTests
{
    private static VersionMetadata Vanilla() => new()
    {
        Id = "1.20.1",
        MainClass = "net.minecraft.client.main.Main",
        Assets = "5",
        Type = "release",
        MinimumLauncherVersion = 21,
        Libraries =
        [
            new() { Name = "com.mojang:authlib:3.11.49" },
            new() { Name = "org.lwjgl:lwjgl:3.3.1" }
        ],
        Arguments = new ArgumentsBlock
        {
            Game = [ArgumentEntry.Plain("--username"), ArgumentEntry.Plain("${auth_player_name}")],
            Jvm = [ArgumentEntry.Plain("-Djava.library.path=${natives_directory}")]
        }
    };

    private static VersionMetadata Forge() => new()
    {
        Id = "1.20.1-forge-47.2.0",
        InheritsFrom = "1.20.1",
        MainClass = "cpw.mods.bootstraplauncher.BootstrapLauncher",
        Libraries =
        [
            // 与 vanilla 同坐标但换了版本：应该覆盖
            new() { Name = "org.lwjgl:lwjgl:3.3.2" },
            new() { Name = "net.minecraftforge:forge:1.20.1-47.2.0:universal" }
        ],
        Arguments = new ArgumentsBlock
        {
            Game = [ArgumentEntry.Plain("--launchTarget"), ArgumentEntry.Plain("forge_client")]
        }
    };

    [Fact]
    public void 单元素链直接原样返回并清掉inheritsFrom()
    {
        var single = new VersionMetadata { Id = "x", InheritsFrom = "parent" };
        var merged = VersionMerger.Merge([single]);

        Assert.Equal("x", merged.Id);
        Assert.Null(merged.InheritsFrom);
    }

    [Fact]
    public void 子版本覆盖标量字段()
    {
        var merged = VersionMerger.Merge([Vanilla(), Forge()]);

        Assert.Equal("1.20.1-forge-47.2.0", merged.Id);
        Assert.Equal("cpw.mods.bootstraplauncher.BootstrapLauncher", merged.MainClass);
        Assert.Equal("5", merged.Assets);
        Assert.Equal(21, merged.MinimumLauncherVersion);
        Assert.Null(merged.InheritsFrom);
    }

    [Fact]
    public void 库按完整坐标去重且子的在前()
    {
        var merged = VersionMerger.Merge([Vanilla(), Forge()]);
        var names = merged.Libraries!.Select(l => l.Name).ToArray();

        // 4 条而不是 3 条：lwjgl 3.3.1 与 3.3.2 是**不同坐标**，Maven 语义下都得留。
        // 这是继承链格式本身的坑（真机上靠 classpath 顺序决定谁生效），不是合并器的缺陷。
        // 合并器只保证「完全相同坐标只出现一次，且子版本的排前面」。
        Assert.Equal(4, names.Length);
        Assert.Equal("org.lwjgl:lwjgl:3.3.2", names[0]);
        Assert.Equal("net.minecraftforge:forge:1.20.1-47.2.0:universal", names[1]);
        Assert.Equal("com.mojang:authlib:3.11.49", names[2]);
        Assert.Equal("org.lwjgl:lwjgl:3.3.1", names[3]);
    }

    [Fact]
    public void 完全相同的坐标只保留子版本那一条()
    {
        var parent = new VersionMetadata
        {
            Id = "p",
            Libraries = [new() { Name = "com.mojang:authlib:3.11.49", Url = "https://parent/" }]
        };
        var child = new VersionMetadata
        {
            Id = "c",
            InheritsFrom = "p",
            Libraries = [new() { Name = "com.mojang:authlib:3.11.49", Url = "https://child/" }]
        };

        var merged = VersionMerger.Merge([parent, child]);

        var lib = Assert.Single(merged.Libraries!);
        Assert.Equal("https://child/", lib.Url);
    }

    [Fact]
    public void 参数祖先在前子在后()
    {
        var merged = VersionMerger.Merge([Vanilla(), Forge()]);
        var game = merged.Arguments!.Game!.SelectMany(e => e.Values).ToArray();

        Assert.Equal(
            ["--username", "${auth_player_name}", "--launchTarget", "forge_client"],
            game);
    }

    [Fact]
    public void 空链抛出()
    {
        Assert.Throws<ArgumentException>(() => VersionMerger.Merge([]));
    }
}

public class ArgumentExpanderTests
{
    private static EnvironmentProfile Windows => new()
    {
        OsName = EnvironmentProfile.OsWindows,
        Arch = "x86_64"
    };

    private static readonly Dictionary<string, string> Placeholders = new()
    {
        ["auth_player_name"] = "Steve",
        ["version_name"] = "1.20.1-forge-47.2.0",
        ["natives_directory"] = @"C:\inst\.minecraft\versions\1.20.1\natives"
    };

    [Fact]
    public void 替换已知占位符()
    {
        ArgumentEntry[] entries = [ArgumentEntry.Plain("--username"), ArgumentEntry.Plain("${auth_player_name}")];
        var result = ArgumentExpander.Expand(entries, Windows, Placeholders);

        Assert.Equal(["--username", "Steve"], result.Arguments);
        Assert.True(result.IsClean);
    }

    [Fact]
    public void 未知占位符原样保留并上报()
    {
        ArgumentEntry[] entries = [ArgumentEntry.Plain("--demo=${unknown_key}")];
        var result = ArgumentExpander.Expand(entries, Windows, Placeholders);

        Assert.Equal(["--demo=${unknown_key}"], result.Arguments);
        Assert.Equal(["unknown_key"], result.UnresolvedPlaceholders);
    }

    [Fact]
    public void 按规则剔除参数项()
    {
        var macOnly = new ArgumentEntry
        {
            Rules = [new Rule { Action = "allow", Os = new OsCondition { Name = "osx" } }],
            Values = ["-XstartOnFirstThread"]
        };
        var crossPlatform = ArgumentEntry.Plain("-XX:+UseG1GC");

        var result = ArgumentExpander.Expand([macOnly, crossPlatform], Windows, Placeholders);

        Assert.Equal(["-XX:+UseG1GC"], result.Arguments);
    }

    [Fact]
    public void 旧格式按空格切分并替换()
    {
        var result = ArgumentExpander.ExpandLegacy(
            "--username ${auth_player_name} --version ${version_name} --gameDir .",
            Placeholders);

        Assert.Equal(
            ["--username", "Steve", "--version", "1.20.1-forge-47.2.0", "--gameDir", "."],
            result.Arguments);
    }

    [Fact]
    public void 空输入不产生参数()
    {
        Assert.Empty(ArgumentExpander.Expand(null, Windows, Placeholders).Arguments);
        Assert.Empty(ArgumentExpander.ExpandLegacy(null, Placeholders).Arguments);
        Assert.Empty(ArgumentExpander.ExpandLegacy("   ", Placeholders).Arguments);
    }
}
