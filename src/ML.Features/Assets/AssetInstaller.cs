using ML.Features.Download;
using ML.Core.Install;

namespace ML.Features.Assets;

public sealed record AssetMaterializeResult
{
    /// <summary>该版本是不是旧版布局。false 时其它字段全为零。</summary>
    public required bool Required { get; init; }

    public required int FilesWritten { get; init; }

    public required int FilesSkipped { get; init; }

    /// <summary>目标根目录：<c>assets/virtual/{id}</c> 或 <c>resources</c>。</summary>
    public string? TargetDirectory { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }
}

public static class AssetInstaller
{
    /// <summary>Mojang 官方资源 CDN。调用方可以换成镜像。</summary>
    public const string ResourceBaseUrl = "https://resources.download.minecraft.net";

    /// <summary>
    /// 生成资源对象的下载任务。URL 规则是
    /// <c>{base}/{hash 前两位}/{hash}</c>，没有目录前缀，也没有按版本分目录。
    /// </summary>
    /// <param name="resourceBaseUrl">
    /// 资源 CDN 基地址。留空用官方地址；镜像源在这里替掉即可——
    /// 这条接缝必须留在内核里，因为「从哪拿字节」和「管哪些镜像」是两件事，
    /// 后者才是插件的事。
    /// </param>
    public static IReadOnlyList<DownloadItem> BuildDownloads(
        GamePaths paths,
        AssetIndex index,
        string? resourceBaseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(index);

        var items = new List<DownloadItem>();
        if (index.Objects is null)
        {
            return items;
        }

        foreach (var (name, asset) in index.Objects)
        {
            _ = name;
            var hash = asset.Hash;
            if (string.IsNullOrEmpty(hash) || hash.Length < 2)
            {
                continue;
            }

            items.Add(new DownloadItem
            {
                Url = ObjectUrl(hash, resourceBaseUrl),
                DestinationPath = paths.AssetObject(hash),
                // 资源文件名就是它自己的 SHA1，所以校验不需要额外的哈希字段。
                Sha1 = hash,
                Size = asset.Size
            });
        }

        return items;
    }

    public static string ObjectUrl(string hash, string? resourceBaseUrl = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        if (hash.Length < 2)
        {
            throw new ArgumentException($"资源哈希过短：{hash}", nameof(hash));
        }

        string root = string.IsNullOrWhiteSpace(resourceBaseUrl)
            ? ResourceBaseUrl
            : resourceBaseUrl.TrimEnd('/');

        return $"{root}/{hash[..2]}/{hash}";
    }

    /// <summary>
    /// 旧版本（1.6 及更早）需要把对象按原名摊到游戏能找到的位置。
    /// <c>map_to_resources</c> 优先于 <c>virtual</c>——两者都写时官方行为是摊到 <c>resources/</c>。
    /// </summary>
    public static AssetMaterializeResult MaterializeLegacy(
        GamePaths paths,
        string indexId,
        AssetIndex index)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        ArgumentNullException.ThrowIfNull(index);

        if (!index.RequiresMaterialization)
        {
            return new AssetMaterializeResult
            {
                Required = false,
                FilesWritten = 0,
                FilesSkipped = 0,
                Warnings = []
            };
        }

        var target = index.MapToResources ? paths.Resources : paths.VirtualAssets(indexId);
        var warnings = new List<string>();
        var written = 0;
        var skipped = 0;

        if (index.Objects is null)
        {
            return new AssetMaterializeResult
            {
                Required = true,
                FilesWritten = 0,
                FilesSkipped = 0,
                TargetDirectory = target,
                Warnings = warnings
            };
        }

        foreach (var (name, asset) in index.Objects)
        {
            var hash = asset.Hash;
            if (string.IsNullOrEmpty(hash) || hash.Length < 2)
            {
                continue;
            }

            var relative = NormalizeRelativeName(name);
            if (relative is null)
            {
                // 索引键来自网络，绝不拿它直接拼路径。
                warnings.Add($"资源键不安全，已跳过：{name}");
                continue;
            }

            var source = paths.AssetObject(hash);
            if (!File.Exists(source))
            {
                warnings.Add($"资源对象缺失：{hash}");
                continue;
            }

            var destination = Path.Combine(target, relative);
            if (File.Exists(destination) && new FileInfo(destination).Length == new FileInfo(source).Length)
            {
                skipped++;
                continue;
            }

            var parent = Path.GetDirectoryName(destination);
            if (parent is not null)
            {
                Directory.CreateDirectory(parent);
            }

            File.Copy(source, destination, overwrite: true);
            written++;
        }

        return new AssetMaterializeResult
        {
            Required = true,
            FilesWritten = written,
            FilesSkipped = skipped,
            TargetDirectory = target,
            Warnings = warnings
        };
    }

    /// <summary>
    /// 把索引键变成安全的相对路径。含 <c>..</c>、绝对路径、盘符、空段的一律返回 <see langword="null"/>。
    /// </summary>
    public static string? NormalizeRelativeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var normalized = name.Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0)
        {
            return null;
        }

        var segments = normalized.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == "." || segment == ".." || segment.Contains(':'))
            {
                return null;
            }
        }

        return Path.Combine(segments);
    }
}
