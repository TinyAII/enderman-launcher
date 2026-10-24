namespace ML.Features.Download;

/// <summary>一条下载任务。</summary>
public sealed record DownloadItem
{
    /// <summary>绝对 URL。</summary>
    public required string Url { get; init; }

    /// <summary>落盘绝对路径。</summary>
    public required string DestinationPath { get; init; }

    /// <summary>期望的 SHA1（40 位十六进制，大小写不敏感）。为 <see langword="null"/> 表示不校验哈希。</summary>
    public string? Sha1 { get; init; }

    /// <summary>期望字节数。为 <see langword="null"/> 表示不校验大小。</summary>
    public long? Size { get; init; }
}
