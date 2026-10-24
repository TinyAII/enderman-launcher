using ML.Core.Install;
using ML.Core.Maven;
using ML.Core.Rules;
using ML.Core.Versioning;
using ML.Features.Assets;
using ML.Features.Download;

namespace ML.Features.Install;

/// <summary>装机阶段。界面按这个分三段进度条，命令行按这个打点。</summary>
public enum InstallStage
{
    /// <summary>校验本地已有的版本元数据。</summary>
    Resolving,

    /// <summary>库文件、client.jar、资源索引、log4j 配置。</summary>
    Libraries,

    /// <summary>natives jar（数量很少，但经常被忽略）。</summary>
    Natives,

    /// <summary>资源对象，装机里最慢的一段。</summary>
    Assets,

    Completed,
}

/// <summary>装机进度。<c>Total</c> 是本阶段的任务数，不是全局。</summary>
public sealed record InstallProgress(
    InstallStage Stage,
    int Completed,
    int Total,
    long BytesDone,
    string? Detail = null)
{
    public double Fraction => Total <= 0 ? 0 : (double)Completed / Total;
}

/// <summary>一次装机的结论。</summary>
public sealed record InstallReport
{
    public required bool Succeeded { get; init; }

    public required TimeSpan Duration { get; init; }

    public required int FilesDownloaded { get; init; }

    public required int FilesSkipped { get; init; }

    public required long Bytes { get; init; }

    public required int AssetObjects { get; init; }

    public NativesExtractResult? Natives { get; init; }

    public IReadOnlyList<DownloadResult> Failures { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// 把一个版本装到磁盘上：库 + client + 资源 + natives + 日志配置。
/// </summary>
/// <remarks>
/// 这里只负责"让磁盘上凑齐启动所需的文件"，不碰实例、不碰账号、不起进程。
/// 拆开的原因是命令行和界面要共用它，而它们对"谁负责进度条"的答案完全不同。
/// </remarks>
public sealed class GameInstaller(GamePaths paths, DownloadQueue queue, string? resourceBaseUrl = null)
{
    private readonly GamePaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    private readonly DownloadQueue _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    private readonly string? _resourceBaseUrl = resourceBaseUrl;

    /// <summary>装机。失败不抛异常——装机失败是常态（网络、磁盘、镜像），用报告回传。</summary>
    public async Task<InstallReport> InstallAsync(
        VersionMetadata version,
        EnvironmentProfile environment,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(environment);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var warnings = new List<string>();

        if (version.InheritsFrom is not null)
        {
            throw new InvalidOperationException(
                $"传给装机的是未合并的清单（inheritsFrom = {version.InheritsFrom}）。先跑 VersionMerger。");
        }

        string versionId = version.Id
            ?? throw new InvalidOperationException("version.json 里没有 id，无法确定目录名。");

        progress?.Report(new InstallProgress(InstallStage.Resolving, 0, 1, 0));

        // 1. 库 + client + 资源索引 + 日志配置
        var baseItems = BuildBaseDownloads(version, environment);
        var (baseReport, baseFailures) = await RunStageAsync(
            InstallStage.Libraries, baseItems, progress, cancellationToken).ConfigureAwait(false);

        if (baseFailures.Count > 0)
        {
            return Fail(started, baseReport, 0, null, baseFailures, warnings);
        }

        // 2. natives jar
        // 内核只负责"选哪些 jar、解压到哪"（NativesPlanner），把"从哪下"留给功能层：
        // Entries 里带着 URL/SHA1，这里翻译成下载任务。内核不认识下载这回事。
        var nativesPlan = NativesPlanner.Build(_paths, version.Libraries ?? [], environment);
        warnings.AddRange(nativesPlan.Warnings);

        var nativeItems = nativesPlan.Entries
            .Where(entry => entry.Url is not null)
            .Select(entry => new DownloadItem
            {
                Url = entry.Url!,
                DestinationPath = _paths.LibraryFile(entry.JarRelativePath),
                Sha1 = entry.Sha1,
                Size = entry.Size
            })
            .ToList();

        var (nativeReport, nativeFailures) = await RunStageAsync(
            InstallStage.Natives, nativeItems, progress, cancellationToken).ConfigureAwait(false);

        if (nativeFailures.Count > 0)
        {
            return Fail(started, Combine(baseReport, nativeReport), 0, null, nativeFailures, warnings);
        }

        var natives = NativesExtractor.Extract(_paths, versionId, nativesPlan);

        // 3. 资源对象
        int assetObjects = 0;
        var assetReport = DownloadReport.Empty;

        string assetIndexId = version.AssetIndex?.Id ?? version.Assets ?? "legacy";
        string assetIndexPath = _paths.AssetIndex(assetIndexId);

        if (!File.Exists(assetIndexPath))
        {
            warnings.Add($"找不到资源索引 {assetIndexPath}，本次不下载资源。缺资源会让游戏进不去世界。");
        }
        else
        {
            var index = AssetIndex.Load(assetIndexPath);
            assetObjects = index.Count;

            // 无论新旧布局，对象文件都按哈希落盘；摊平是下完之后的额外一步。
            var assetItems = AssetInstaller.BuildDownloads(_paths, index, _resourceBaseUrl);

            (assetReport, var assetFailures) = await RunStageAsync(
                InstallStage.Assets, assetItems, progress, cancellationToken).ConfigureAwait(false);

            // 旧版布局（virtual / map_to_resources）下载完还要按原名摊平，
            // 否则游戏按 "minecraft/sounds/..." 找文件会找不到。
            if (index.RequiresMaterialization)
            {
                var materialized = AssetInstaller.MaterializeLegacy(_paths, assetIndexId, index);
                warnings.AddRange(materialized.Warnings);
            }

            if (assetFailures.Count > 0)
            {
                var combined = Combine(Combine(baseReport, nativeReport), assetReport);
                return Fail(started, combined, assetObjects, natives, assetFailures, warnings);
            }
        }

        progress?.Report(new InstallProgress(InstallStage.Completed, 1, 1, 0));

        var all = Combine(Combine(baseReport, nativeReport), assetReport);
        started.Stop();

        return new InstallReport
        {
            Succeeded = true,
            Duration = started.Elapsed,
            FilesDownloaded = all.Results.Count(r => r.Outcome == DownloadOutcome.Downloaded),
            FilesSkipped = all.Results.Count(r => r.Outcome == DownloadOutcome.AlreadyValid),
            Bytes = all.TotalBytes,
            AssetObjects = assetObjects,
            Natives = natives,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// 列出"要让这个版本能跑起来，磁盘上应该有这些文件"——只算清单，不下载。
    /// </summary>
    /// <remarks>
    /// 启动前的快速检查用它：逐个 stat + 比大小，几千个文件 1 秒内扫完。
    /// 补全时也用它：缺哪几个就下哪几个，不用把整个装机流程跑一遍
    /// （那会让每个文件都走一遍 SHA1 校验，慢得多）。
    /// </remarks>
    public IReadOnlyList<DownloadItem> PlanDownloads(VersionMetadata version, EnvironmentProfile environment)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(environment);

        var items = new List<DownloadItem>(BuildBaseDownloads(version, environment));

        var nativesPlan = NativesPlanner.Build(_paths, version.Libraries ?? [], environment);
        items.AddRange(nativesPlan.Entries
            .Where(entry => entry.Url is not null)
            .Select(entry => new DownloadItem
            {
                Url = entry.Url!,
                DestinationPath = _paths.LibraryFile(entry.JarRelativePath),
                Sha1 = entry.Sha1,
                Size = entry.Size
            }));

        // 资源对象。索引缺失时跳过——装机流程遇到同样情况也只是给个警告。
        string assetIndexId = version.AssetIndex?.Id ?? version.Assets ?? "legacy";
        string assetIndexPath = _paths.AssetIndex(assetIndexId);

        if (File.Exists(assetIndexPath))
        {
            var index = AssetIndex.Load(assetIndexPath);
            items.AddRange(AssetInstaller.BuildDownloads(_paths, index, _resourceBaseUrl));
        }

        return items;
    }

    /// <summary>库 + client + 资源索引 + 日志配置的下载任务。</summary>
    internal List<DownloadItem> BuildBaseDownloads(VersionMetadata version, EnvironmentProfile environment)
    {
        var items = new List<DownloadItem>();

        foreach (var library in version.Libraries ?? [])
        {
            if (!RuleEvaluator.IsAllowed(library.Rules, environment))
            {
                continue;
            }

            if (library.Downloads?.Artifact is { } artifact)
            {
                Add(artifact.Url, "libraries/" + artifact.Path, artifact.Sha1, artifact.Size);
            }
            else if (!string.IsNullOrEmpty(library.Url) &&
                     MavenCoordinate.TryParse(library.Name, out var coordinate))
            {
                Add(
                    library.Url.TrimEnd('/') + "/" + coordinate.RelativePath,
                    "libraries/" + coordinate.RelativePath,
                    library.Sha1,
                    library.Size);
            }
        }

        if (version.Downloads is { } downloads && downloads.TryGetValue("client", out var client))
        {
            Add(client.Url, $"versions/{version.Id}/{version.Id}.jar", client.Sha1, client.Size);
        }

        if (version.AssetIndex is { } assetIndex)
        {
            Add(assetIndex.Url, $"assets/indexes/{assetIndex.Id}.json", assetIndex.Sha1, assetIndex.Size);
        }

        if (version.Logging?.Client?.File is { } logConfig &&
            !string.IsNullOrEmpty(logConfig.Id))
        {
            Add(logConfig.Url, $"assets/log_configs/{logConfig.Id}", logConfig.Sha1, logConfig.Size);
        }

        return items;

        void Add(string? url, string relativePath, string? sha1, long? size)
        {
            if (string.IsNullOrEmpty(url))
            {
                return;
            }

            items.Add(new DownloadItem
            {
                Url = url,
                DestinationPath = Path.Combine(
                    _paths.Root,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)),
                Sha1 = sha1,
                Size = size,
            });
        }
    }

    private async Task<(DownloadReport Report, IReadOnlyList<DownloadResult> Failures)> RunStageAsync(
        InstallStage stage,
        IReadOnlyList<DownloadItem> items,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return (DownloadReport.Empty, []);
        }

        long bytesDone = 0;

        var stageProgress = progress is null
            ? null
            : new SyncProgress<DownloadProgress>(update =>
            {
                bytesDone += update.Result.Bytes;
                progress.Report(new InstallProgress(
                    stage,
                    update.Completed,
                    update.Total,
                    bytesDone,
                    Path.GetFileName(update.Result.Item.DestinationPath)));
            });

        var report = await _queue
            .RunAsync(items, DownloadQueue.DefaultConcurrency, stageProgress, cancellationToken)
            .ConfigureAwait(false);

        return (report, report.Failures);
    }

    private static InstallReport Fail(
        System.Diagnostics.Stopwatch started,
        DownloadReport report,
        int assetObjects,
        NativesExtractResult? natives,
        IReadOnlyList<DownloadResult> failures,
        IReadOnlyList<string> warnings)
    {
        started.Stop();

        return new InstallReport
        {
            Succeeded = false,
            Duration = started.Elapsed,
            FilesDownloaded = report.Results.Count(r => r.Outcome == DownloadOutcome.Downloaded),
            FilesSkipped = report.Results.Count(r => r.Outcome == DownloadOutcome.AlreadyValid),
            Bytes = report.TotalBytes,
            AssetObjects = assetObjects,
            Natives = natives,
            Failures = failures,
            Warnings = warnings,
        };
    }

    private static DownloadReport Combine(DownloadReport left, DownloadReport right) =>
        new([.. left.Results, .. right.Results]);
}

/// <summary>同步投递的 <see cref="IProgress{T}"/>。</summary>
/// <remarks>
/// <see cref="Progress{T}"/> 把回调 Post 到同步上下文异步投递，装机结束时最后一条可能还没到，
/// 进度界面就会永远停在 99%。这里直接同步调用，语义可预期。
/// </remarks>
internal sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
