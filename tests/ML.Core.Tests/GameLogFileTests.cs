using ML.Core.Launch;

namespace ML.Core.Tests;

/// <summary>
/// 游戏日志落盘。文件名、写入、释放都能直接测；清理跳过的逻辑在宿主里。
/// </summary>
public sealed class GameLogFileTests
{
    [Fact]
    public void 文件名带实例名和时间戳()
    {
        string root = TempDir();

        try
        {
            using var log = GameLogFile.Create(root, "生存服", new DateTimeOffset(2026, 10, 24, 11, 4, 5, TimeSpan.FromHours(8)));

            Assert.True(File.Exists(log.Path));
            Assert.Equal("生存服-20261024-110405.log", Path.GetFileName(log.Path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 实例名里的非法字符会被替换()
    {
        Assert.Equal("a_b_c", GameLogFile.Sanitize("a:b/c"));
        Assert.Equal("instance", GameLogFile.Sanitize("   "));
    }

    [Fact]
    public void 写入的行带时间戳和来源()
    {
        string root = TempDir();

        try
        {
            using (var log = GameLogFile.Create(root, "测试", DateTimeOffset.Now))
            {
                log.Write("game", "hello minecraft");
            }

            string text = File.ReadAllText(Directory.GetFiles(root, "*.log").Single());

            Assert.Contains("[game] hello minecraft", text);
            Assert.Matches(@"^\[\d{2}:\d{2}:\d{2}\.\d{3}\] \[game\]", text.TrimStart('\uFEFF'));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 释放之后不再算正在写入()
    {
        string root = TempDir();

        try
        {
            var log = GameLogFile.Create(root, "测试", DateTimeOffset.Now);

            Assert.True(log.IsActive);
            log.Dispose();
            Assert.False(log.IsActive);

            // 句柄放开之后文件可以删掉——清理日志靠的就是这一点
            File.Delete(log.Path);
            Assert.False(File.Exists(log.Path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 同一秒内重复启动不会覆盖上一个文件()
    {
        string root = TempDir();
        var stamp = new DateTimeOffset(2026, 10, 24, 11, 4, 5, TimeSpan.Zero);

        try
        {
            using var first = GameLogFile.Create(root, "生存服", stamp);
            using var second = GameLogFile.Create(root, "生存服", stamp);

            Assert.NotEqual(first.Path, second.Path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string TempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "ml-log-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);

        return path;
    }
}
