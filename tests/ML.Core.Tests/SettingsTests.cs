using ML.Core.Configuration;
using ML.Core.Install;

namespace ML.Core.Tests;

/// <summary>
/// 设置的读写。重点不在「能存能读」，在<b>坏输入与坏文件时不能把用户的设置弄丢</b>。
/// </summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ml-settings-" + Guid.NewGuid().ToString("n"));

    private SettingsStore NewStore() => new(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void 没有配置文件时返回默认值且不抛()
    {
        var store = NewStore();
        LauncherSettings settings = store.Load();

        Assert.Null(settings.GameRoot);
        Assert.Equal("Player", settings.PlayerName);
        Assert.Equal(2048, settings.MemoryMb);
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public void 存了再读回来字段一致()
    {
        var store = NewStore();
        var written = new LauncherSettings
        {
            GameRoot = @"D:\MC\.minecraft",
            PlayerName = "Steve",
            MemoryMb = 8192,
            WindowWidth = 1280,
            WindowHeight = 720,
            JavaHome = @"C:\Program Files\Zulu\zulu-17",
            LastVersionId = "1.20.1",
            ExtraJvmArguments = ["-XX:+UseG1GC"]
        };

        store.Save(written);

        var reloaded = NewStore().Load();

        Assert.Equal(written.GameRoot, reloaded.GameRoot);
        Assert.Equal("Steve", reloaded.PlayerName);
        Assert.Equal(8192, reloaded.MemoryMb);
        Assert.Equal(1280, reloaded.WindowWidth);
        Assert.Equal("1.20.1", reloaded.LastVersionId);
        Assert.Equal(["-XX:+UseG1GC"], reloaded.ExtraJvmArguments);
    }

    [Fact]
    public void 只写了一部分字段时其余用默认值()
    {
        // 配置文件是人手能改的，也常被「只想改一个值」的人写成只有一两行。
        // 缺字段必须落回默认值，而不是变成 0 之后被 Normalize 夹到最小值——
        // 那会让用户看到「窗口 320×320」这种没人设置过的值。
        var store = NewStore();
        Directory.CreateDirectory(_root);
        File.WriteAllText(store.FilePath, """{ "gameRoot": "D:\\MC\\.minecraft", "memoryMb": 3072 }""");

        LauncherSettings settings = store.Load();

        Assert.Equal(@"D:\MC\.minecraft", settings.GameRoot);
        Assert.Equal(3072, settings.MemoryMb);
        Assert.Equal(854, settings.WindowWidth);
        Assert.Equal(480, settings.WindowHeight);
        Assert.Equal("Player", settings.PlayerName);
        Assert.Empty(settings.ExtraJvmArguments);
    }

    [Fact]
    public void 配置文件损坏时回落到默认值并把原文件改名备份()
    {
        var store = NewStore();
        Directory.CreateDirectory(_root);
        File.WriteAllText(store.FilePath, "{ 这不是 JSON");

        LauncherSettings settings = store.Load();

        Assert.Equal("Player", settings.PlayerName);
        Assert.False(File.Exists(store.FilePath));
        Assert.True(File.Exists(store.BrokenFilePath), "坏文件必须被改名保留，不能静默删掉");
        Assert.Contains("这不是 JSON", File.ReadAllText(store.BrokenFilePath));
    }

    [Theory]
    [InlineData(-1, LauncherSettings.DefaultMemoryMb)]
    [InlineData(0, LauncherSettings.DefaultMemoryMb)]
    [InlineData(1, LauncherSettings.MinMemoryMb)]
    [InlineData(999999, LauncherSettings.MaxMemoryMb)]
    [InlineData(4096, 4096)]
    public void 内存零值落回默认_越界被夹取(int input, int expected)
    {
        var settings = new LauncherSettings { MemoryMb = input }.Normalize();
        Assert.Equal(expected, settings.MemoryMb);
    }

    [Fact]
    public void 窗口尺寸为零或负数时各自落回各自的默认值()
    {
        foreach (int input in new[] { 0, -5 })
        {
            var settings = new LauncherSettings { WindowWidth = input, WindowHeight = input }.Normalize();
            Assert.Equal(LauncherSettings.DefaultWindowWidth, settings.WindowWidth);
            Assert.Equal(LauncherSettings.DefaultWindowHeight, settings.WindowHeight);
        }
    }

    [Theory]
    [InlineData(100, LauncherSettings.MinWindowSide)]
    [InlineData(99999, LauncherSettings.MaxWindowSide)]
    [InlineData(1280, 1280)]
    public void 窗口尺寸越界被夹取(int input, int expected)
    {
        var settings = new LauncherSettings { WindowWidth = input, WindowHeight = input }.Normalize();
        Assert.Equal(expected, settings.WindowWidth);
        Assert.Equal(expected, settings.WindowHeight);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("有 空格")]
    [InlineData("-Xmx1G")]
    public void 非法玩家名回落到_Player(string? raw)
    {
        Assert.Equal("Player", LauncherSettings.NormalizePlayerName(raw));
    }

    [Fact]
    public void 玩家名超长被截断到16()
    {
        string name = LauncherSettings.NormalizePlayerName("ABCDEFGHIJKLMNOPQRSTUVWXYZ");
        Assert.Equal(16, name.Length);
        Assert.Equal("ABCDEFGHIJKLMNOP", name);
    }

    [Fact]
    public void 空白的JVM参数被丢掉()
    {
        var settings = new LauncherSettings
        {
            ExtraJvmArguments = ["  -Xss1M  ", "", "   ", "-Dfoo=bar"]
        }.Normalize();

        Assert.Equal(["-Xss1M", "-Dfoo=bar"], settings.ExtraJvmArguments);
    }

    [Fact]
    public void 保存之后不留下临时文件()
    {
        var store = NewStore();
        store.Save(new LauncherSettings { PlayerName = "Alex" });

        Assert.True(File.Exists(store.FilePath));
        Assert.False(File.Exists(store.FilePath + ".tmp"), "原子写的临时文件必须已经被 move 掉");
    }

    [Fact]
    public void Update只改指定字段其余保持()
    {
        var store = NewStore();
        store.Save(new LauncherSettings { PlayerName = "Alex", MemoryMb = 4096 });

        LauncherSettings updated = store.Update(current => current with { GameRoot = @"E:\MC" });

        Assert.Equal(@"E:\MC", updated.GameRoot);
        Assert.Equal("Alex", updated.PlayerName);
        Assert.Equal(4096, updated.MemoryMb);
        Assert.Equal(@"E:\MC", NewStore().Load().GameRoot);
    }

    [Fact]
    public void 游戏目录为空白时视为未指定()
    {
        var settings = new LauncherSettings { GameRoot = "   " }.Normalize();
        Assert.Null(settings.GameRoot);
        Assert.False(settings.HasGameRoot);
    }
}

/// <summary>
/// 游戏目录发现。这部分决定「用户第一次打开能不能看见自己已经装好的版本」，
/// 所以判断有效性的证据必须宽松但可靠：宁可多认一个目录（用户可以换），
/// 不可漏认（用户会以为启动器看不见他的游戏）。
/// </summary>
public sealed class GameRootLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ml-gameroot-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static string MakeVersion(string gameRoot, string id)
    {
        string dir = Path.Combine(gameRoot, "versions", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, id + ".json"), "{}");
        return dir;
    }

    [Fact]
    public void 有合法版本目录即算游戏目录()
    {
        string root = Path.Combine(_root, "mc");
        MakeVersion(root, "1.20.1");

        Assert.True(GameRootLocator.LooksLikeGameRoot(root));
        Assert.Equal(1, GameRootLocator.CountVersions(root));
    }

    [Fact]
    public void 目录名与json名不一致的残版本不计入()
    {
        string root = Path.Combine(_root, "broken");
        Directory.CreateDirectory(Path.Combine(root, "versions", "1.20.1"));

        Assert.Equal(0, GameRootLocator.CountVersions(root));
        Assert.False(GameRootLocator.LooksLikeGameRoot(root));
    }

    [Fact]
    public void 只有libraries也算游戏目录()
    {
        string root = Path.Combine(_root, "libs");
        Directory.CreateDirectory(Path.Combine(root, "libraries"));

        Assert.True(GameRootLocator.LooksLikeGameRoot(root));
    }

    [Fact]
    public void 只有launcher_profiles也算游戏目录()
    {
        string root = Path.Combine(_root, "profiles");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "launcher_profiles.json"), "{}");

        Assert.True(GameRootLocator.LooksLikeGameRoot(root));
    }

    [Fact]
    public void 不存在的目录不算()
    {
        Assert.False(GameRootLocator.LooksLikeGameRoot(Path.Combine(_root, "nope")));
    }

    [Fact]
    public void 环境变量指定的目录优先级最高且去重()
    {
        string appData = Path.Combine(_root, "appdata");
        string minecraft = Path.Combine(appData, ".minecraft");
        MakeVersion(minecraft, "1.20.1");

        string portable = Path.Combine(_root, "data", ".minecraft");
        MakeVersion(portable, "1.19.4");

        var env = new Dictionary<string, string?>
        {
            ["APPDATA"] = appData,
            ["USERPROFILE"] = Path.Combine(_root, "home"),
            [GameRootLocator.GameDirEnvironmentVariable] = portable
        };

        var candidates = GameRootLocator.Detect(Path.Combine(_root, "data"), env);

        // 显式环境变量排第一
        Assert.Equal(Path.GetFullPath(portable), candidates[0].Path);
        Assert.Contains("ML_GAME_DIR", candidates[0].Source);

        // %APPDATA%\.minecraft 与 %USERPROFILE%\AppData\Roaming\.minecraft 在本例里不是同一个目录，
        // 但同一个路径只能出现一次
        var paths = candidates.Select(c => c.Path).ToList();
        Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void 推荐版本最多的有效目录()
    {
        string small = Path.Combine(_root, "small");
        MakeVersion(small, "1.20.1");

        string big = Path.Combine(_root, "big");
        MakeVersion(big, "1.20.1");
        MakeVersion(big, "1.19.4");
        MakeVersion(big, "1.18.2");

        var best = GameRootLocator.Best([
            new GameRootCandidate(small, "测试", GameRootLocator.LooksLikeGameRoot(small), GameRootLocator.CountVersions(small)),
            new GameRootCandidate(big, "测试", GameRootLocator.LooksLikeGameRoot(big), GameRootLocator.CountVersions(big)),
        ]);

        Assert.NotNull(best);
        Assert.Equal(Path.GetFullPath(big), best.Path);
        Assert.Equal(3, best.VersionCount);
    }

    [Fact]
    public void 没有有效目录时推荐为null()
    {
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);

        Assert.Null(GameRootLocator.Best([
            new GameRootCandidate(empty, "测试", false, 0)
        ]));
    }
}
