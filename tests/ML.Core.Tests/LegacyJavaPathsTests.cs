using ML.Core.Launch;

namespace ML.Core.Tests;

/// <summary>
/// 「Java 18 以下 + 中文路径」兼容。以前这段逻辑埋在主机里，
/// 没有 Java 8 的机器就测不了；抽出来之后可以在真实的中文目录上验证。
/// </summary>
public sealed class LegacyJavaPathsTests
{
    [Fact]
    public void 新版Java不需要处理()
    {
        var result = LegacyJavaPaths.Resolve(@"C:\程序\java\bin\java.exe", @"D:\游戏\实例\game", 21, compatibilityEnabled: true, linksRoot: null);

        Assert.Equal(LegacyPathMode.NotNeeded, result.Mode);
        Assert.Equal(@"C:\程序\java\bin\java.exe", result.JavaExecutable);
    }

    [Fact]
    public void 纯英文路径不需要处理()
    {
        var result = LegacyJavaPaths.Resolve(@"C:\Java\jdk8\bin\java.exe", @"D:\mc\instance\game", 8, compatibilityEnabled: true, linksRoot: null);

        Assert.Equal(LegacyPathMode.NotNeeded, result.Mode);
    }

    [Fact]
    public void 关掉兼容就原样返回()
    {
        var result = LegacyJavaPaths.Resolve(@"C:\程序\java\bin\java.exe", @"D:\游戏\game", 8, compatibilityEnabled: false, linksRoot: null);

        Assert.Equal(LegacyPathMode.NotNeeded, result.Mode);
        Assert.Equal(@"C:\程序\java\bin\java.exe", result.JavaExecutable);
        Assert.Equal(@"D:\游戏\game", result.GameDirectory);
    }

    [Fact]
    public void Java8加中文目录会映射成ASCII链接()
    {
        // 真实场景：中文的游戏目录 + Java 8。这里造一个真目录，验证链接确实建起来了。
        string root = Path.Combine(Path.GetTempPath(), "ML中文路径测试" + Guid.NewGuid().ToString("N")[..6]);
        string gameDir = Path.Combine(root, "存档目录");
        Directory.CreateDirectory(gameDir);

        // 链接目录必须是 ASCII：用临时目录下的一层纯英文名字
        string linksRoot = Path.Combine(Path.GetTempPath(), "ml-links-" + Guid.NewGuid().ToString("N")[..6]);

        try
        {
            var result = LegacyJavaPaths.Resolve(
                @"C:\Program Files\Java\jdk8in\java.exe",
                gameDir,
                javaMajor: 8,
                compatibilityEnabled: true,
                linksRoot: linksRoot);

            Assert.Equal(LegacyPathMode.Applied, result.Mode);
            Assert.True(ShortPath.IsAscii(result.GameDirectory), $"游戏目录仍非 ASCII：{result.GameDirectory}");

            // 链接要真的能走通：通过链接写文件，原目录里能看到
            File.WriteAllText(Path.Combine(result.GameDirectory, "probe.txt"), "ok");
            Assert.True(File.Exists(Path.Combine(gameDir, "probe.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);

            if (Directory.Exists(linksRoot))
            {
                // 链接目录只删链接本身
                foreach (string child in Directory.GetDirectories(linksRoot))
                {
                    Directory.Delete(child, recursive: false);
                }

                Directory.Delete(linksRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void 没有链接目录时如实报告不可用()
    {
        var result = LegacyJavaPaths.Resolve(
            @"C:\Program Files\Java\jdk8in\java.exe",
            @"D:\游戏\存档",
            javaMajor: 8,
            compatibilityEnabled: true,
            linksRoot: null);

        Assert.Equal(LegacyPathMode.Unavailable, result.Mode);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public void 短路径对ASCII路径不做改动()
    {
        Assert.Equal(@"C:\Windows", ShortPath.TryGet(@"C:\Windows"));
    }
}
