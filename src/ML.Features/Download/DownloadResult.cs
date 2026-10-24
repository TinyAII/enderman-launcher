namespace ML.Features.Download;

/// <summary>单个文件的下载结局。</summary>
public enum DownloadOutcome
{
    /// <summary>已下载并通过校验。</summary>
    Downloaded,

    /// <summary>本地文件已存在且校验通过，一个请求都没发。</summary>
    AlreadyValid,

    /// <summary>下载完成（或重试用尽），但 SHA1 或大小不符。</summary>
    HashMismatch,

    /// <summary>网络或磁盘错误，重试用尽。</summary>
    Failed
}

/// <summary>单个文件的下载结果。</summary>
public sealed record DownloadResult
{
    public required DownloadItem Item { get; init; }

    public required DownloadOutcome Outcome { get; init; }

    /// <summary>实际尝试次数。跳过时为 0。</summary>
    public int Attempts { get; init; }

    /// <summary>落盘后的字节数。</summary>
    public long Bytes { get; init; }

    /// <summary>失败原因，成功时为 <see langword="null"/>。</summary>
    public string? Error { get; init; }

    public bool Succeeded =>
        Outcome is DownloadOutcome.Downloaded or DownloadOutcome.AlreadyValid;
}
