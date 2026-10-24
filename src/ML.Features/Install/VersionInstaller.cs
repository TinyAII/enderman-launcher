using ML.Core.Install;
using ML.Features.Assets;
using ML.Features.Download;
using ML.Core.Rules;
using ML.Core.Versioning;
using ML.Features.Mojang;

namespace ML.Features.Install;

/// <summary>装一个原本没装过的版本：先取 version.json，再把文件全下下来。</summary>
public sealed record VersionInstallResult(
    bool Ok,
    string VersionId,
    InstallReport? Report,
    string? Error,
    int DownloadedFiles = 0);

/// <summary>
/// 从 Mojang 装一个版本到本地游戏目录。
/// </summary>
/// <remarks>
/// <para>
/// 分成两步是必须的：<b>没有 version.json 就不知道要下什么</b>。
/// 所以先把它落到 <c>versions/&lt;id&gt;/&lt;id&gt;.json</c>，再交给
/// <see cref="GameInstaller"/> 按清单补齐库、客户端、资源、natives。
/// </para>
/// <para>
/// 只处理原版（没有 <c>inheritsFrom</c>）。带继承的是 Forge / Fabric 那种，
/// 得先装父版本再合并清单（VersionMerger 已存在，接进来是下一步）。
/// </para>
/// </remarks>
public sealed class VersionInstaller(
    GamePaths paths,
    VersionManifestClient client,
    GameInstaller installer)
{
    private readonly GamePaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    private readonly VersionManifestClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly GameInstaller _installer = installer ?? throw new ArgumentNullException(nameof(installer));

    /// <summary>
    /// 装一个版本。已经有 version.json 时只补文件，不重复下载清单。
    /// </summary>
    public async Task<VersionInstallResult> InstallAsync(
        string versionId,
        EnvironmentProfile environment,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<string, IReadOnlyList<DownloadItem>, long, int, CancellationToken, Task<(bool Ok, long Bytes)>>? downloadThrough = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        ArgumentNullException.ThrowIfNull(environment);

        string jsonPath = _paths.VersionJson(versionId);

        try
        {
            if (!File.Exists(jsonPath))
            {
                VersionManifestEntry entry = await _client.FindAsync(versionId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Mojang 的版本清单里没有 {versionId}。");

                string json = await _client.FetchVersionJsonAsync(entry, cancellationToken).ConfigureAwait(false);

                Directory.CreateDirectory(_paths.VersionDirectory(versionId));
                await File.WriteAllTextAsync(jsonPath, json, cancellationToken).ConfigureAwait(false);
            }

            VersionMetadata version = System.Text.Json.JsonSerializer
                .Deserialize<VersionMetadata>(await File.ReadAllTextAsync(jsonPath, cancellationToken).ConfigureAwait(false))
                ?? throw new InvalidDataException($"version.json 解析失败：{jsonPath}");

            if (version.InheritsFrom is { Length: > 0 } parent)
            {
                // 清单没合并，装机那边会拒绝。这里给一句人话，而不是抛底层异常。
                return new VersionInstallResult(
                    false, versionId, null, $"{versionId} 继承自 {parent}，需要先装父版本（加载器安装还没接）。");
            }

            // 走下载队列：任务会出现在「下载」页，用户能看进度、能暂停、能限速。
            // 不下队列时仍然可以跑完整装机（命令行/插件没给队列的兜底）。
            if (downloadThrough is not null)
            {
                IReadOnlyList<DownloadItem> plan = _installer.PlanDownloads(version, environment);

                progress?.Report(new InstallProgress(InstallStage.Resolving, 0, plan.Count, 0));

                // 先把"已经存在且大小正确"的部分记进进度：否则装一个已装过的版本，
                // 进度条会一直显示 0%（因为一个字节都不用下）。
                AuditReport audit = FileAudit.QuickCheck(plan, cancellationToken);
                long healthyBytes = plan.Sum(item => item.Size ?? 0) - audit.BrokenBytes;
                int healthyFiles = plan.Count - audit.Broken.Count;

                (bool ok, long downloaded) = await downloadThrough(
                    $"安装 {versionId}", plan, healthyBytes, healthyFiles, cancellationToken).ConfigureAwait(false);

                if (!ok)
                {
                    return new VersionInstallResult(
                        false, versionId, null, "下载没有完成（可能被暂停或取消了）。");
                }

                // 后处理不在下载队列里：它是本地解压/铺文件，入队反而看不出意义
                progress?.Report(new InstallProgress(InstallStage.Natives, 0, 1, 0));

                var nativesPlan = NativesPlanner.Build(_paths, version.Libraries ?? [], environment);
                NativesExtractor.Extract(_paths, versionId, nativesPlan);

                string assetIndexId = version.AssetIndex?.Id ?? version.Assets ?? "legacy";
                string assetIndexPath = _paths.AssetIndex(assetIndexId);

                if (File.Exists(assetIndexPath))
                {
                    AssetIndex index = AssetIndex.Load(assetIndexPath);

                    if (index.RequiresMaterialization)
                    {
                        AssetInstaller.MaterializeLegacy(_paths, assetIndexId, index);
                    }
                }

                progress?.Report(new InstallProgress(InstallStage.Completed, 1, 1, 0));

                return new VersionInstallResult(true, versionId, null, null, audit.Broken.Count);
            }

            InstallReport report = await _installer
                .InstallAsync(version, environment, progress, cancellationToken)
                .ConfigureAwait(false);

            return report.Succeeded
                ? new VersionInstallResult(true, versionId, report, null, report.FilesDownloaded)
                : new VersionInstallResult(
                    false,
                    versionId,
                    report,
                    report.Failures.Count > 0 ? report.Failures[0].Error : "有文件没下成功。");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or InvalidOperationException or IOException)
        {
            return new VersionInstallResult(false, versionId, null, ex.Message);
        }
    }
}
