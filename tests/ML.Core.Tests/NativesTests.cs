using System.IO.Compression;
using System.Text;
using ML.Features.Download;
using ML.Core.Install;
using ML.Core.Rules;
using ML.Core.Versioning;

namespace ML.Core.Tests;

public sealed class NativesTests
{
    private const string VanillaFile = "vanilla-1.20.1.json";

    [Fact]
    public void 六十四位_Windows_只挑不带架构后缀的_natives()
    {
        var version = Fixtures.LoadVersion(VanillaFile);
        using var game = new TempGame();

        var plan = NativesPlanner.Build(game.Paths, version.Libraries!, Env("x86_64"));

        Assert.NotEmpty(plan.Entries);

        foreach (var entry in plan.Entries)
        {
            Assert.True(
                NativeClassifier.MatchesArchitecture(entry.Classifier, Env("x86_64")),
                $"{entry.LibraryName} 的 {entry.Classifier} 不属于 x86_64");
            Assert.DoesNotContain("-x86", entry.Classifier, StringComparison.Ordinal);
            Assert.DoesNotContain("-arm64", entry.Classifier, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 这是本模块存在的理由：1.20.1 元数据里三个 Windows natives 的 rules 全部只写
    /// <c>{"os":{"name":"windows"}}</c>，规则层面三条全放行，必须靠架构把它们分开。
    /// </summary>
    [Fact]
    public void 元数据里三条_Windows_natives_规则全放行_必须靠架构拦掉两条()
    {
        var version = Fixtures.LoadVersion(VanillaFile);
        using var game = new TempGame();
        var env = Env("x86_64");

        var allowedByRules = version.Libraries!
            .Where(library => RuleEvaluator.IsAllowed(library.Rules, env))
            .Where(library => NativeClassifier.IsModernNatives(library.Name))
            .ToArray();

        var plan = NativesPlanner.Build(game.Paths, version.Libraries!, env);

        Assert.True(
            allowedByRules.Length > plan.Entries.Count,
            "如果规则本身已经能过滤架构，这个测试就失去意义了");

        Assert.Contains(plan.SkippedByArchitecture, name => name.Contains("-x86", StringComparison.Ordinal));
        Assert.Contains(plan.SkippedByArchitecture, name => name.Contains("-arm64", StringComparison.Ordinal));
    }

    [Fact]
    public void arm64_机器只挑_arm64()
    {
        var version = Fixtures.LoadVersion(VanillaFile);
        using var game = new TempGame();

        var plan = NativesPlanner.Build(game.Paths, version.Libraries!, Env("arm64"));

        Assert.NotEmpty(plan.Entries);
        Assert.All(plan.Entries, entry => Assert.EndsWith("-arm64", entry.Classifier, StringComparison.Ordinal));
    }

    [Fact]
    public void x86_机器只挑_x86()
    {
        var version = Fixtures.LoadVersion(VanillaFile);
        using var game = new TempGame();

        var plan = NativesPlanner.Build(game.Paths, version.Libraries!, Env("x86"));

        Assert.NotEmpty(plan.Entries);
        Assert.All(plan.Entries, entry => Assert.EndsWith("-x86", entry.Classifier, StringComparison.Ordinal));
    }

    [Fact]
    public void 现代格式的_natives_要进_classpath_且都有下载地址()
    {
        var version = Fixtures.LoadVersion(VanillaFile);
        using var game = new TempGame();

        var plan = NativesPlanner.Build(game.Paths, version.Libraries!, Env("x86_64"));

        Assert.All(plan.Entries, entry => Assert.NotNull(entry.Url));
        Assert.All(plan.Entries, entry => Assert.NotNull(entry.Sha1));
    }

    [Fact]
    public void 旧式映射里的_arch_占位符展开成_64_或_32()
    {
        var library = LegacyLibrary("natives-windows-${arch}", "natives-windows-64");

        Assert.Equal("natives-windows-64", NativeClassifier.ResolveClassifierKey(library, Env("x86_64")));
        Assert.Equal("natives-windows-32", NativeClassifier.ResolveClassifierKey(library, Env("x86")));
    }

    [Fact]
    public void 旧式映射可以不带占位符()
    {
        var library = LegacyLibrary("natives-windows", "natives-windows");

        Assert.Equal("natives-windows", NativeClassifier.ResolveClassifierKey(library, Env("x86_64")));
    }

    [Fact]
    public void 旧式映射的_osx_键能命中()
    {
        var library = LegacyLibrary("natives-macos", "natives-macos");

        var env = new EnvironmentProfile
        {
            OsName = EnvironmentProfile.OsMacOs,
            OsVersion = "14.0",
            Arch = "x86_64"
        };

        Assert.Equal("natives-macos", NativeClassifier.ResolveClassifierKey(library, env));
    }

    [Fact]
    public void 旧式映射指向的分类器不存在时要给出警告而不是静默跳过()
    {
        var library = LegacyLibrary("natives-windows-${arch}", "完全不存在的键");
        using var game = new TempGame();

        var plan = NativesPlanner.Build(game.Paths, [library], Env("x86_64"));

        Assert.Empty(plan.Entries);
        Assert.Contains(plan.Warnings, warning => warning.Contains("classifiers", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("natives-windows", "x86_64", true)]
    [InlineData("natives-windows-64", "x86_64", true)]
    [InlineData("natives-windows-32", "x86_64", false)]
    [InlineData("natives-windows-32", "x86", true)]
    [InlineData("natives-windows-arm64", "x86_64", false)]
    [InlineData("natives-windows-arm64", "arm64", true)]
    [InlineData("natives-macos", "x86_64", true)]
    [InlineData("natives-macos-arm64", "x86_64", false)]
    [InlineData("natives-linux", "x86_64", true)]
    public void 架构判定同时认现代与旧式两套词汇(string classifier, string arch, bool expected)
    {
        Assert.Equal(expected, NativeClassifier.MatchesArchitecture(classifier, Env(arch)));
    }

    [Fact]
    public void 解压只留文件名并丢掉_META_INF()
    {
        using var game = new TempGame();
        WriteJar(game.Jar("org/example/natives/1.0/natives-1.0-natives-windows.jar"),
            ("lwjgl.dll", "dll"),
            ("META-INF/MANIFEST.MF", "manifest"));

        var plan = TempGame.Plan("org/example/natives/1.0/natives-1.0-natives-windows.jar");
        var result = NativesExtractor.Extract(game.Paths, "1.20.1", plan);

        Assert.True(result.Rebuilt);
        Assert.Equal(["lwjgl.dll"], result.Files);

        var natives = game.Paths.Natives("1.20.1");
        Assert.True(File.Exists(Path.Combine(natives, "lwjgl.dll")));
        Assert.False(Directory.Exists(Path.Combine(natives, "META-INF")));
    }

    [Fact]
    public void 声明过的排除项生效()
    {
        using var game = new TempGame();
        WriteJar(game.Jar("a/natives.jar"), ("keep.dll", "x"), ("skipme/data.bin", "y"));

        var plan = TempGame.PlanExcluding(["skipme/"], "a/natives.jar");
        var result = NativesExtractor.Extract(game.Paths, "v", plan);

        Assert.Contains("keep.dll", result.Files);
        Assert.DoesNotContain("data.bin", result.Files);
    }

    [Fact]
    public void 两个_jar_撞同名文件时先到先得并记录冲突()
    {
        using var game = new TempGame();
        WriteJar(game.Jar("a/first.jar"), ("same.dll", "first"));
        WriteJar(game.Jar("a/second.jar"), ("same.dll", "second"));

        var result = NativesExtractor.Extract(game.Paths, "v", TempGame.Plan("a/first.jar", "a/second.jar"));

        Assert.Single(result.Files);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("same.dll", conflict.FileName);
        Assert.Equal("a/first.jar", conflict.KeptFrom);
        Assert.Equal("a/second.jar", conflict.IgnoredFrom);

        Assert.Equal("first", File.ReadAllText(Path.Combine(game.Paths.Natives("v"), "same.dll")));
    }

    [Fact]
    public void 清单命中时整轮解压被跳过()
    {
        using var game = new TempGame();
        WriteJar(game.Jar("a/natives.jar"), ("lwjgl.dll", "x"));

        var plan = TempGame.Plan("a/natives.jar");
        var first = NativesExtractor.Extract(game.Paths, "v", plan);
        var second = NativesExtractor.Extract(game.Paths, "v", plan);

        Assert.True(first.Rebuilt);
        Assert.Equal(1, first.FilesWritten);

        Assert.False(second.Rebuilt);
        Assert.Equal(0, second.FilesWritten);
        Assert.Equal(1, second.FilesSkipped);
    }

    [Fact]
    public void jar_集合变化时清空重解_旧文件不残留()
    {
        using var game = new TempGame();
        WriteJar(game.Jar("a/one.jar"), ("one.dll", "1"));
        WriteJar(game.Jar("a/two.jar"), ("two.dll", "2"));

        NativesExtractor.Extract(game.Paths, "v", TempGame.Plan("a/one.jar"));
        var second = NativesExtractor.Extract(game.Paths, "v", TempGame.Plan("a/two.jar"));

        Assert.True(second.Rebuilt);
        Assert.False(File.Exists(Path.Combine(game.Paths.Natives("v"), "one.dll")));
        Assert.True(File.Exists(Path.Combine(game.Paths.Natives("v"), "two.dll")));
    }

    [Fact]
    public void 清单里的文件被删掉后会重解()
    {
        using var game = new TempGame();
        WriteJar(game.Jar("a/natives.jar"), ("lwjgl.dll", "x"));
        var plan = TempGame.Plan("a/natives.jar");

        NativesExtractor.Extract(game.Paths, "v", plan);
        File.Delete(Path.Combine(game.Paths.Natives("v"), "lwjgl.dll"));

        var again = NativesExtractor.Extract(game.Paths, "v", plan);

        Assert.True(again.Rebuilt);
        Assert.True(File.Exists(Path.Combine(game.Paths.Natives("v"), "lwjgl.dll")));
    }

    [Fact]
    public void zip_里的路径穿越被压平成文件名()
    {
        using var game = new TempGame();
        WriteJar(game.Jar("a/evil.jar"), ("../../../evil.dll", "payload"));

        var result = NativesExtractor.Extract(game.Paths, "v", TempGame.Plan("a/evil.jar"));

        Assert.Equal(["evil.dll"], result.Files);

        var natives = game.Paths.Natives("v");
        Assert.True(File.Exists(Path.Combine(natives, "evil.dll")));
        Assert.False(File.Exists(Path.Combine(game.Paths.Root, "evil.dll")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(game.Paths.Root)!, "evil.dll")));
    }

    [Fact]
    public void jar_缺失时给警告而不是抛异常()
    {
        using var game = new TempGame();

        var result = NativesExtractor.Extract(game.Paths, "v", TempGame.Plan("a/根本不存在的.jar"));

        Assert.Empty(result.Files);
        Assert.Contains(result.Warnings, warning => warning.Contains("缺失", StringComparison.Ordinal));
    }

    [Fact]
    public void forge_继承链合并后也能算出_natives()
    {
        var vanilla = Fixtures.LoadVersion(VanillaFile);
        var forge = Fixtures.LoadVersion("forge-1.20.1-47.2.0.json");
        var merged = VersionMerger.Merge([vanilla, forge]);
        using var game = new TempGame();

        var plan = NativesPlanner.Build(game.Paths, merged.Libraries!, Env("x86_64"));

        Assert.NotEmpty(plan.Entries);
    }

    private static EnvironmentProfile Env(string arch) => new()
    {
        OsName = EnvironmentProfile.OsWindows,
        OsVersion = "10.0.26200",
        Arch = arch,
        Features = new Dictionary<string, bool>(0)
    };

    private static LibraryEntry LegacyLibrary(string template, string classifierKey) => new()
    {
        Name = "org.example:legacy-natives:1.0",
        Natives = new Dictionary<string, string>
        {
            ["windows"] = template,
            ["osx"] = template
        },
        Downloads = new LibraryDownloads
        {
            Classifiers = new Dictionary<string, DownloadRef>
            {
                [classifierKey] = new()
                {
                    Path = $"org/example/legacy-natives/1.0/legacy-natives-1.0-{classifierKey}.jar",
                    Url = $"https://example.invalid/{classifierKey}.jar",
                    Size = 1
                }
            }
        }
    };

    private static void WriteJar(string path, params (string Name, string Content)[] entries)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);

        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var stream = entry.Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }
    }

    /// <summary>一次性临时游戏目录。它只造计划和 jar，不做任何联网动作。</summary>
    private sealed class TempGame : IDisposable
    {
        public TempGame()
        {
            Paths = new GamePaths(Path.Combine(Path.GetTempPath(), "ml-natives-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(Paths.Root);
        }

        public GamePaths Paths { get; }

        /// <summary>相对路径 → <c>libraries/</c> 下的绝对路径（顺带建目录）。</summary>
        public string Jar(string jarRelativePath)
        {
            var full = Paths.LibraryFile(jarRelativePath);
            var directory = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            return full;
        }

        public static NativesPlan Plan(params string[] jarRelativePaths) => PlanExcluding([], jarRelativePaths);

        public static NativesPlan PlanExcluding(
            IReadOnlyList<string> excludes,
            params string[] jarRelativePaths)
        {
            var entries = jarRelativePaths
                .Select(path => new NativesEntry
                {
                    LibraryName = path,
                    Classifier = "natives-windows",
                    JarRelativePath = path,
                    // 清单只把 Sha1 当身份令牌用，这里直接拿路径当令牌，不必真算哈希。
                    Sha1 = path,
                    Excludes = excludes
                })
                .ToArray();

            return new NativesPlan
            {
                Entries = entries,
                SkippedByArchitecture = [],
                Warnings = []
            };
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Paths.Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
