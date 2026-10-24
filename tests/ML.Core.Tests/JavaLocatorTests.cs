using ML.Core.Java;

namespace ML.Core.Tests;

public sealed class JavaLocatorTests
{
    [Theory]
    [InlineData("1.8.0_402", 8)]
    [InlineData("1.7.0_80", 7)]
    [InlineData("1.6.0_45", 6)]
    [InlineData("9", 9)]
    [InlineData("17", 17)]
    [InlineData("17.0.9", 17)]
    [InlineData("21.0.10", 21)]
    [InlineData("21.0.10+7-LTS", 21)]
    [InlineData("25-ea", 25)]
    [InlineData("\"21.0.10\"", 21)]
    public void 版本号解析覆盖新旧格式(string raw, int expectedMajor)
    {
        Assert.True(JavaVersion.TryParse(raw, out var version));
        Assert.Equal(expectedMajor, version.Major);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("ea")]
    [InlineData("---")]
    public void 非法版本号被拒绝(string? raw)
    {
        Assert.False(JavaVersion.TryParse(raw, out var version));
        Assert.False(version.IsKnown);
    }

    [Fact]
    public void 同主版本内按数字比大小而不是按字符串()
    {
        Assert.True(JavaVersion.TryParse("17.0.9", out var older));
        Assert.True(JavaVersion.TryParse("17.0.17", out var newer));

        // 按字符串比 "17.0.9" > "17.0.17"，这里必须是反的。
        Assert.True(newer.CompareTo(older) > 0);
        Assert.True(older.CompareTo(newer) < 0);
        Assert.Equal(0, older.CompareTo(older));
    }

    [Fact]
    public void 主版本优先于分量()
    {
        Assert.True(JavaVersion.TryParse("8", out var eight));
        Assert.True(JavaVersion.TryParse("21.0.1", out var twentyOne));
        Assert.True(twentyOne.CompareTo(eight) > 0);
    }

    [Fact]
    public void release文件解析引号注释与CRLF()
    {
        const string content =
            "# 注释行\r\n" +
            "IMPLEMENTOR=\"Eclipse Adoptium\"\r\n" +
            "JAVA_VERSION=\"21.0.10\"\r\n" +
            "JAVA_VERSION_DATE=\"2025-10-21\"\r\n" +
            "OS_ARCH=\"x86_64\"\r\n" +
            "OS_NAME=Windows\r\n" +
            "EMPTY_LINE_CHECK=\r\n" +
            "\r\n";

        var release = JavaRelease.Parse(content);

        Assert.Equal("Eclipse Adoptium", release.Implementor);
        Assert.Equal("21.0.10", release.VersionString);
        Assert.Equal("x86_64", release.OsArch);
        // 不带引号也要能读
        Assert.Equal("Windows", release.OsName);
        Assert.Null(release.RuntimeName);
    }

    [Fact]
    public void release文件解析转义()
    {
        var release = JavaRelease.Parse("IMPLEMENTOR=\"A\\\\B\"\n");

        Assert.Equal("A\\B", release.Implementor);
    }

    [Theory]
    [InlineData("x86_64", JavaArchitecture.X64)]
    [InlineData("amd64", JavaArchitecture.X64)]
    [InlineData("AARCH64", JavaArchitecture.Arm64)]
    [InlineData("x86", JavaArchitecture.X86)]
    [InlineData("i686", JavaArchitecture.X86)]
    [InlineData("sparc", JavaArchitecture.Unknown)]
    [InlineData(null, null)]
    public void 架构映射(string? raw, JavaArchitecture? expected)
    {
        Assert.Equal(expected, JavaLocator.ParseArchitecture(raw));
    }

    [Fact]
    public void 合法JavaHome能被识破()
    {
        using var home = SyntheticHome.Create("21.0.10", "Eclipse Adoptium", "x86_64");

        var installation = JavaLocator.Inspect(home.Path);

        Assert.NotNull(installation);
        Assert.Equal(21, installation.Major);
        Assert.Equal("Eclipse Adoptium", installation.Vendor);
        Assert.Equal(JavaArchitecture.X64, installation.Architecture);
        Assert.True(installation.HasReleaseFile);
        Assert.True(File.Exists(installation.ExecutablePath));
    }

    [Fact]
    public void 目录少了java可执行文件就不算JavaHome()
    {
        using var home = SyntheticHome.Create("21.0.10", "X", "x86_64", withExecutable: false);

        Assert.Null(JavaLocator.Inspect(home.Path));
    }

    [Fact]
    public void 既没有release文件又跑不起来的一律拒绝()
    {
        using var home = SyntheticHome.Create("21.0.10", "X", "x86_64", withRelease: false);

        Assert.Null(JavaLocator.Inspect(home.Path));
    }

    [Fact]
    public void 尾部分隔符与被遮蔽的符号链接不影响识别()
    {
        using var home = SyntheticHome.Create("17.0.9", "Zulu", "x86_64");

        var plain = JavaLocator.Inspect(home.Path);
        var withSeparator = JavaLocator.Inspect(home.Path + Path.DirectorySeparatorChar);

        Assert.NotNull(plain);
        Assert.NotNull(withSeparator);
        Assert.Equal(plain.Home, withSeparator.Home);
    }

    [Fact]
    public void 精确匹配优先于更高版本()
    {
        var installations = new[]
        {
            Fake(21, x64, JavaSource.Registry),
            Fake(17, x64, JavaSource.Registry)
        };

        var chosen = JavaLocator.SelectBest(installations, 17);

        Assert.NotNull(chosen);
        Assert.Equal(17, chosen.Major);
    }

    [Fact]
    public void 没有精确匹配时选最小的更高版本()
    {
        var installations = new[]
        {
            Fake(21, x64, JavaSource.Registry),
            Fake(25, x64, JavaSource.Registry),
            Fake(8, x64, JavaSource.Registry)
        };

        var chosen = JavaLocator.SelectBest(installations, 17);

        Assert.NotNull(chosen);
        Assert.Equal(21, chosen.Major);
    }

    [Fact]
    public void 只有一个更高版本时就用它()
    {
        var installations = new[] { Fake(8, x64, JavaSource.Registry), Fake(21, x64, JavaSource.Registry) };

        var chosen = JavaLocator.SelectBest(installations, 17);

        Assert.NotNull(chosen);
        Assert.Equal(21, chosen.Major);
    }

    [Fact]
    public void 版本不够的一律不选()
    {
        var installations = new[] { Fake(8, x64, JavaSource.Registry), Fake(11, x64, JavaSource.Registry) };

        Assert.Null(JavaLocator.SelectBest(installations, 17));
    }

    [Fact]
    public void 位数不匹配的候选被排除()
    {
        var installations = new[] { Fake(17, JavaArchitecture.X86, JavaSource.Registry) };

        Assert.Null(JavaLocator.SelectBest(installations, 17, JavaArchitecture.X64));
        Assert.NotNull(JavaLocator.SelectBest(installations, 17, JavaArchitecture.X86));
        // 不指定位数时不做过滤
        Assert.NotNull(JavaLocator.SelectBest(installations, 17));
    }

    [Fact]
    public void 同一主版本优先选可信来源()
    {
        var installations = new[]
        {
            Fake(21, x64, JavaSource.Registry),
            Fake(21, x64, JavaSource.JavaHome)
        };

        var chosen = JavaLocator.SelectBest(installations, 21);

        Assert.NotNull(chosen);
        Assert.Equal(JavaSource.JavaHome, chosen.Source);
    }

    [Fact]
    public void 同目录只报一次()
    {
        using var home = SyntheticHome.Create("21.0.10", "Eclipse Adoptium", "x86_64");

        var installations = JavaLocator.Discover(new JavaDiscoveryOptions
        {
            IncludeJavaHome = false,
            IncludePath = false,
            IncludeRegistry = false,
            IncludeWellKnownDirectories = false,
            IncludeMojangRuntimes = false,
            IncludeOtherLaunchers = false,
            AdditionalHomes = [home.Path, home.Path + Path.DirectorySeparatorChar, home.Path.ToUpperInvariant()]
        });

        Assert.Single(installations);
        Assert.Equal(21, installations[0].Major);
    }

    [Fact]
    public void 多余候选被静默忽略()
    {
        var installations = JavaLocator.Discover(new JavaDiscoveryOptions
        {
            IncludeJavaHome = false,
            IncludePath = false,
            IncludeRegistry = false,
            IncludeWellKnownDirectories = false,
            IncludeMojangRuntimes = false,
            IncludeOtherLaunchers = false,
            AdditionalHomes = [@"C:\肯定不存在的目录\jdk", "", "   "]
        });

        Assert.Empty(installations);
    }

    /// <summary>这台机器上必须能扫出至少一个真 Java，且每个结果都自洽。</summary>
    [Fact]
    public void 真机上能扫出可用的Java()
    {
        var installations = JavaLocator.Discover();

        Assert.NotEmpty(installations);

        foreach (var installation in installations)
        {
            Assert.True(installation.Version.IsKnown, $"{installation.Home} 版本不可用");
            Assert.True(File.Exists(installation.ExecutablePath), $"{installation.ExecutablePath} 不存在");
            Assert.True(File.Exists(Path.Combine(installation.Home, "bin", "java.exe")));
        }

        // 同一目录不能出现两次。
        var keys = installations.Select(i => i.Home.ToLowerInvariant()).ToArray();
        Assert.Equal(keys.Length, keys.Distinct().Count());

        // 版本必须是从高到低。
        for (var i = 1; i < installations.Count; i++)
        {
            Assert.True(installations[i - 1].Version.CompareTo(installations[i].Version) >= 0);
        }

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome) && Directory.Exists(javaHome))
        {
            var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(javaHome));
            Assert.Contains(
                installations,
                installation => string.Equals(installation.Home, expected, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static readonly JavaArchitecture x64 = JavaArchitecture.X64;

    [Fact]
    public void 自动选择优先度最高的那个()
    {
        // 列表顺序：17 排在 21 前面，但实例要求 21 —— 17 不满足，应该跳过 17 选 21
        var installations = new[]
        {
            Fake(17, x64, JavaSource.Registry),
            Fake(21, x64, JavaSource.Registry)
        };

        var chosen = JavaLocator.SelectBest(
            installations,
            21,
            preferredOrder: [installations[0].Home, installations[1].Home]);

        Assert.NotNull(chosen);
        Assert.Equal(21, chosen.Major);
    }

    [Fact]
    public void 优先度顺序压过版本高低()
    {
        // 两个都满足 17 的要求：按用户排的顺序，22 在 25 前面，就该选 22
        var installations = new[]
        {
            Fake(22, x64, JavaSource.Registry),
            Fake(25, x64, JavaSource.Registry)
        };

        var chosen = JavaLocator.SelectBest(
            installations,
            17,
            preferredOrder: [installations[0].Home, installations[1].Home]);

        Assert.NotNull(chosen);
        Assert.Equal(22, chosen.Major);
    }

    [Fact]
    public void 没有排序时仍按版本挑()
    {
        var installations = new[]
        {
            Fake(22, x64, JavaSource.Registry),
            Fake(25, x64, JavaSource.Registry)
        };

        var chosen = JavaLocator.SelectBest(installations, 17);

        Assert.NotNull(chosen);
        Assert.Equal(22, chosen.Major);
    }

    private static JavaInstallation Fake(int major, JavaArchitecture architecture, JavaSource source)
    {
        var version = new JavaVersion(major, major.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return new JavaInstallation
        {
            Home = $@"C:\fake\jdk-{major}",
            ExecutablePath = $@"C:\fake\jdk-{major}\bin\java.exe",
            Version = version,
            Architecture = architecture,
            Source = source,
            HasReleaseFile = true
        };
    }

    /// <summary>造一个假的 Java 根目录，只放 <c>Inspect</c> 需要看的东西。</summary>
    private sealed class SyntheticHome : IDisposable
    {
        private SyntheticHome(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static SyntheticHome Create(
            string version,
            string implementor,
            string osArch,
            bool withExecutable = true,
            bool withRelease = true)
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ml-java-" + Guid.NewGuid().ToString("N"));
            var bin = System.IO.Path.Combine(root, "bin");
            Directory.CreateDirectory(bin);

            if (withExecutable)
            {
                // 内容无所谓，Inspect 只做 File.Exists；故意留空，让版本探测必然失败。
                File.WriteAllBytes(System.IO.Path.Combine(bin, "java.exe"), []);
            }

            if (withRelease)
            {
                File.WriteAllText(
                    System.IO.Path.Combine(root, "release"),
                    $"IMPLEMENTOR=\"{implementor}\"\n" +
                    $"JAVA_VERSION=\"{version}\"\n" +
                    $"JAVA_VERSION_DATE=\"2025-10-21\"\n" +
                    $"OS_ARCH=\"{osArch}\"\n" +
                    "OS_NAME=\"Windows\"\n");
            }

            return new SyntheticHome(root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
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
