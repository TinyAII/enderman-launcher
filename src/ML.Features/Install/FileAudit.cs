using System.Security.Cryptography;
using ML.Features.Download;

namespace ML.Features.Install;

/// <summary>一次文件审计的结论。</summary>
public sealed record AuditReport
{
    /// <summary>清单里的文件总数。</summary>
    public required int Total { get; init; }

    /// <summary>缺失或不符的文件（可以直接丢给下载器补）。</summary>
    public required IReadOnlyList<DownloadItem> Broken { get; init; }

    /// <summary>缺失数量（文件根本不在）。</summary>
    public required int Missing { get; init; }

    /// <summary>大小不对的数量。</summary>
    public required int WrongSize { get; init; }

    /// <summary>哈希不对的数量（只有完整校验才会有）。</summary>
    public required int WrongHash { get; init; }

    public bool Healthy => Broken.Count == 0;

    public long BrokenBytes => Broken.Sum(item => item.Size ?? 0);
}

/// <summary>
/// 检查磁盘上的文件是否齐全。
/// </summary>
/// <remarks>
/// <para>
/// 两档，代价差两个数量级：
/// <list type="bullet">
/// <item><b>快速</b>：stat + 比大小。几千个文件 1 秒内，启动前跑这一档。</item>
/// <item><b>完整</b>：逐个读出来算 SHA1。总量几 GB，SSD 几十秒、机械盘几分钟，手动触发。</item>
/// </list>
/// </para>
/// <para>
/// 快速档抓得住"文件被删、被覆盖成别的东西、下了一半"这些常见情况；
/// 抓不住"大小一样但内容坏了"——那需要完整档。
/// </para>
/// </remarks>
public static class FileAudit
{
    /// <summary>快速检查：文件在不在、大小对不对。</summary>
    public static AuditReport QuickCheck(IReadOnlyList<DownloadItem> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        var broken = new List<DownloadItem>();
        int missing = 0;
        int wrongSize = 0;

        foreach (DownloadItem item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = new FileInfo(item.DestinationPath);

            if (!file.Exists)
            {
                broken.Add(item);
                missing++;
                continue;
            }

            if (item.Size is { } expected && file.Length != expected)
            {
                broken.Add(item);
                wrongSize++;
            }
        }

        return new AuditReport
        {
            Total = items.Count,
            Broken = broken,
            Missing = missing,
            WrongSize = wrongSize,
            WrongHash = 0
        };
    }

    /// <summary>完整校验：逐个算 SHA1。只对带哈希的条目有效，没哈希的退回比大小。</summary>
    public static async Task<AuditReport> FullCheckAsync(
        IReadOnlyList<DownloadItem> items,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        var broken = new List<DownloadItem>();
        int missing = 0;
        int wrongSize = 0;
        int wrongHash = 0;

        for (int index = 0; index < items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DownloadItem item = items[index];

            if (!File.Exists(item.DestinationPath))
            {
                broken.Add(item);
                missing++;
            }
            else if (await MatchesAsync(item, cancellationToken).ConfigureAwait(false))
            {
                // 对得上，什么都不做
            }
            else if (item.Sha1 is { Length: > 0 })
            {
                broken.Add(item);
                wrongHash++;
            }
            else
            {
                broken.Add(item);
                wrongSize++;
            }

            progress?.Report(index + 1);
        }

        return new AuditReport
        {
            Total = items.Count,
            Broken = broken,
            Missing = missing,
            WrongSize = wrongSize,
            WrongHash = wrongHash
        };
    }

    private static async Task<bool> MatchesAsync(DownloadItem item, CancellationToken cancellationToken)
    {
        var file = new FileInfo(item.DestinationPath);

        if (item.Size is { } expected && file.Length != expected)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(item.Sha1))
        {
            return true;
        }

        await using var stream = new FileStream(
            item.DestinationPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);

        byte[] hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);

        return Convert.ToHexString(hash).Equals(item.Sha1.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
