namespace ML.Features.Download;

public enum DownloadTaskState
{
    Queued,
    Running,
    Paused,
    Done,
    Failed,
    Canceled
}

/// <summary>
/// 一次"要装的东西"。
/// </summary>
/// <remarks>
/// <b>版本 + 加载器是一个任务，不是两个。</b> 用户心智里"装个 1.21.8 的 Fabric"是一件事；
/// 拆成两个任务会出现"版本下完、加载器还在一半"的中间态，进度条也不知道该显示谁的。
/// </remarks>
public sealed class DownloadTask
{
    private long _doneBytes;
    private int _doneFiles;

    /// <param name="alreadyDoneBytes">已存在且校验通过的文件字节数——先记进进度，别让进度条停在 0%。</param>
    /// <param name="alreadyDoneFiles">同上，文件数。</param>
    public DownloadTask(
        string title,
        IReadOnlyList<DownloadItem> items,
        long alreadyDoneBytes = 0,
        int alreadyDoneFiles = 0)
    {
        Id = Guid.NewGuid().ToString("N")[..12];
        Title = title;
        Items = items;
        TotalBytes = items.Sum(item => item.Size ?? 0);
        CreatedAt = DateTimeOffset.Now;

        _doneBytes = alreadyDoneBytes;
        _doneFiles = alreadyDoneFiles;
    }

    public string Id { get; }

    public string Title { get; }

    public IReadOnlyList<DownloadItem> Items { get; }

    /// <summary>预期总字节。未知大小的文件不计入，所以这是个下限。</summary>
    public long TotalBytes { get; private set; }

    public long DoneBytes => Interlocked.Read(ref _doneBytes);

    public int FilesTotal => Items.Count;

    public int FilesDone => Volatile.Read(ref _doneFiles);

    public DownloadTaskState State { get; internal set; } = DownloadTaskState.Queued;

    public string? Error { get; internal set; }

    public DateTimeOffset CreatedAt { get; }

    internal CancellationTokenSource? Cancellation { get; set; }

    internal bool PauseRequested { get; set; }

    internal void AddBytes(long bytes) => Interlocked.Add(ref _doneBytes, bytes);

    internal void MarkFileDone() => Interlocked.Increment(ref _doneFiles);

    public DownloadTaskSnapshot Snapshot() => new(
        Id,
        Title,
        State.ToString(),
        TotalBytes,
        DoneBytes,
        FilesTotal,
        FilesDone,
        Error,
        CreatedAt);
}

/// <summary>
/// 给界面用的快照。状态给的是枚举名而不是数字——JSON 里数字没人看得懂，前端也难匹配。
/// </summary>
public sealed record DownloadTaskSnapshot(
    string Id,
    string Title,
    string State,
    long TotalBytes,
    long DoneBytes,
    int FilesTotal,
    int FilesDone,
    string? Error,
    DateTimeOffset CreatedAt);
