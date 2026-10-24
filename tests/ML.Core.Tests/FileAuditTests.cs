using ML.Features.Download;
using ML.Features.Install;

namespace ML.Core.Tests;

/// <summary>
/// 文件审计。启动前的"缺没缺"就靠它，所以两档都要能准确分类。
/// </summary>
public sealed class FileAuditTests
{
    [Fact]
    public void 快速检查能分出缺失与大小不符()
    {
        string root = TempDir();

        try
        {
            string good = Path.Combine(root, "good.jar");
            string wrong = Path.Combine(root, "wrong.jar");
            string missing = Path.Combine(root, "missing.jar");

            File.WriteAllText(good, "12345");
            File.WriteAllText(wrong, "123");

            var items = new[]
            {
                new DownloadItem { Url = "https://x/1", DestinationPath = good, Size = 5 },
                new DownloadItem { Url = "https://x/2", DestinationPath = wrong, Size = 99 },
                new DownloadItem { Url = "https://x/3", DestinationPath = missing, Size = 10 }
            };

            AuditReport report = FileAudit.QuickCheck(items);

            Assert.Equal(3, report.Total);
            Assert.Equal(1, report.Missing);
            Assert.Equal(1, report.WrongSize);
            Assert.Equal(2, report.Broken.Count);
            Assert.False(report.Healthy);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 文件都在且大小对就是健康()
    {
        string root = TempDir();

        try
        {
            string file = Path.Combine(root, "a.jar");
            File.WriteAllText(file, "abc");

            AuditReport report = FileAudit.QuickCheck([
                new DownloadItem { Url = "https://x/1", DestinationPath = file, Size = 3 }
            ]);

            Assert.True(report.Healthy);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task 完整校验能抓出大小对但内容坏的()
    {
        string root = TempDir();

        try
        {
            string file = Path.Combine(root, "bad.jar");

            // 大小对得上，但内容不是这个哈希对应的东西
            File.WriteAllText(file, "abc");

            var item = new DownloadItem
            {
                Url = "https://x/1",
                DestinationPath = file,
                Size = 3,
                Sha1 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            };

            AuditReport quick = FileAudit.QuickCheck([item]);
            Assert.True(quick.Healthy);  // 快速档看不出来

            AuditReport full = await FileAudit.FullCheckAsync([item]);
            Assert.Equal(1, full.WrongHash);
            Assert.False(full.Healthy);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>真算一遍 SHA1，确认完整档不是"永远返回通过"。</summary>
    [Fact]
    public async Task 哈希对得上才算通过()
    {
        string root = TempDir();

        try
        {
            string file = Path.Combine(root, "ok.jar");
            File.WriteAllText(file, "hello");

            string sha1 = Convert.ToHexStringLower(
                System.Security.Cryptography.SHA1.HashData("hello"u8.ToArray()));

            AuditReport report = await FileAudit.FullCheckAsync([
                new DownloadItem { Url = "https://x/1", DestinationPath = file, Size = 5, Sha1 = sha1 }
            ]);

            Assert.True(report.Healthy);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string TempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "ml-audit-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);

        return path;
    }
}
