// CA1711：名字以 Queue 结尾会被当成集合类型。这里确实是「排队执行下载」的语义，保留。
#pragma warning disable CA1711

namespace ML.Features.Download;

/// <summary>单条进度回报。</summary>
public sealed record DownloadProgress(int Completed, int Total, DownloadResult Result);

/// <summary>一批下载的汇总。</summary>
public sealed record DownloadReport(IReadOnlyList<DownloadResult> Results)
{
    /// <summary>空批次（某阶段没有任何任务时用它，省掉调用方到处判 null）。</summary>
    public static DownloadReport Empty { get; } = new([]);

    public int Total => Results.Count;

    public int Succeeded => Results.Count(result => result.Succeeded);

    public IReadOnlyList<DownloadResult> Failures =>
        Results.Where(result => !result.Succeeded).ToArray();

    public bool AllSucceeded => Failures.Count == 0;

    /// <summary>本批**实际传输**的字节数。</summary>
    /// <remarks>
    /// 只算新下载的。已经存在的文件也有大小，但它们一个字节都没走网络；
    /// 把它们算进来会让「本轮下载了 621 MB」这种显示在第二次启动时原地不动，
    /// 用户看到的是"明明全跳过了却还在下载"。
    /// </remarks>
    public long TotalBytes => Results
        .Where(result => result.Outcome == DownloadOutcome.Downloaded)
        .Sum(result => result.Bytes);
}

/// <summary>有并发上限的批量下载器。</summary>
public sealed class DownloadQueue(FileDownloader downloader)
{
    /// <summary>
    /// 默认并发。实测并发 8/16/32 对吞吐没有可辨别的影响（瓶颈在线路，见 docs/性能基线.md），
    /// 所以取一个不把连接池和镜像打满的小值。
    /// </summary>
    public const int DefaultConcurrency = 8;

    public FileDownloader Downloader { get; } = downloader;

    /// <summary>
    /// 跑完一批下载。同一落盘路径只保留第一条——两个不同 URL 抢同一个目标文件是调用方的 bug，
    /// 并发写同一个 <c>.part</c> 只会互相踩。
    /// </summary>
    public async Task<DownloadReport> RunAsync(
        IEnumerable<DownloadItem> items,
        int concurrency = DefaultConcurrency,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);

        var unique = new Dictionary<string, DownloadItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            unique.TryAdd(item.DestinationPath, item);
        }

        var list = unique.Values.ToArray();
        var results = new DownloadResult[list.Length];
        var completed = 0;

        using var gate = new SemaphoreSlim(concurrency, concurrency);

        var tasks = list.Select(async (item, index) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await Downloader
                    .DownloadAsync(item, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                results[index] = result;
                progress?.Report(new DownloadProgress(
                    Interlocked.Increment(ref completed), list.Length, result));
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        return new DownloadReport(results);
    }
}
