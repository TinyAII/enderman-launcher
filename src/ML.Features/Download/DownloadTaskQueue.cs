using System.Net;

namespace ML.Features.Download;

/// <summary>
/// 任务队列：管"同时跑几个任务"、暂停/恢复/取消，文件怎么下交给 <see cref="FileDownloader"/>。
/// </summary>
/// <remarks>
/// <para>
/// 目标路径由调用方给：版本文件落到 <c>versions/...</c>、自定义下载落到 <c>downloads/</c>、
/// 插件可能有自己的结构。这一层不认识这些布局。
/// </para>
/// <para>
/// 进度事件做了节流（250 ms）：一个任务几千个文件，每读 64 KB 就推一次，前端会被刷死。
/// 状态变化立即推，不节流。
/// </para>
/// </remarks>
public sealed class DownloadTaskQueue : IAsyncDisposable
{
    /// <summary>排队上限。点得再快也不该让队列无限涨。</summary>
    public const int MaxQueuedTasks = 500;

    private readonly HttpClient _http;
    private readonly RateLimiter _limiter;
    private readonly FileDownloader _downloader;

    private readonly List<DownloadTask> _tasks = [];
    private readonly object _gate = new();

    private readonly Action<DownloadTaskSnapshot[]> _emit;
    private readonly Timer _progressTimer;

    private int _maxParallelTasks = 1;
    private int _threadsPerTask = 64;
    private int _running;

    public DownloadTaskQueue(Action<DownloadTaskSnapshot[]> emit, long bytesPerSecond = 0)
    {
        ArgumentNullException.ThrowIfNull(emit);

        _emit = emit;
        _limiter = new RateLimiter(bytesPerSecond);

        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 64,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.None
        };

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        _downloader = new FileDownloader(_http, _limiter);

        _progressTimer = new Timer(_ => EmitProgress(), null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
    }

    /// <summary>状态/进度变化，整体快照。</summary>
    public event Action<DownloadTaskSnapshot[]>? Changed;

    /// <summary>同时在跑几个任务（设置里 1/2/3/5/8/10）。</summary>
    public int MaxParallelTasks
    {
        get => Volatile.Read(ref _maxParallelTasks);
        set => Volatile.Write(ref _maxParallelTasks, Math.Clamp(value, 1, 10));
    }

    /// <summary>每个任务内部同时下几个文件（设置里 64/256）。</summary>
    public int ThreadsPerTask
    {
        get => Volatile.Read(ref _threadsPerTask);
        set => Volatile.Write(ref _threadsPerTask, Math.Clamp(value, 1, 256));
    }

    /// <summary>全局限速（字节/秒）。0 = 不限。</summary>
    public long BytesPerSecond
    {
        get => _limiter.BytesPerSecond;
        set => _limiter.Configure(value);
    }

    public IReadOnlyList<DownloadTaskSnapshot> Snapshot()
    {
        lock (_gate)
        {
            return [.. _tasks.Select(task => task.Snapshot())];
        }
    }

    /// <summary>排一个任务。返回任务 id；队列满了返回 null。</summary>
    public string? Enqueue(
        string title,
        IReadOnlyList<DownloadItem> items,
        long alreadyDoneBytes = 0,
        int alreadyDoneFiles = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count == 0)
        {
            throw new ArgumentException("任务里没有任何文件。", nameof(items));
        }

        DownloadTask task;

        lock (_gate)
        {
            int active = _tasks.Count(t => t.State is DownloadTaskState.Queued or DownloadTaskState.Running);

            if (active >= MaxQueuedTasks)
            {
                return null;
            }

            task = new DownloadTask(title, items, alreadyDoneBytes, alreadyDoneFiles);
            _tasks.Add(task);
        }

        Emit();
        _ = Task.Run(PumpAsync);

        return task.Id;
    }

    public bool Pause(string id)
    {
        lock (_gate)
        {
            DownloadTask? task = _tasks.FirstOrDefault(t => t.Id == id);

            if (task is null || task.State is not (DownloadTaskState.Queued or DownloadTaskState.Running))
            {
                return false;
            }

            task.PauseRequested = true;
            task.State = DownloadTaskState.Paused;
            task.Cancellation?.Cancel();
        }

        Emit();

        return true;
    }

    public bool Resume(string id)
    {
        lock (_gate)
        {
            DownloadTask? task = _tasks.FirstOrDefault(t => t.Id == id);

            if (task is null || task.State != DownloadTaskState.Paused)
            {
                return false;
            }

            task.PauseRequested = false;
            task.State = DownloadTaskState.Queued;
            task.Error = null;
        }

        Emit();
        _ = Task.Run(PumpAsync);

        return true;
    }

    public bool Cancel(string id)
    {
        lock (_gate)
        {
            DownloadTask? task = _tasks.FirstOrDefault(t => t.Id == id);

            if (task is null || task.State is DownloadTaskState.Done or DownloadTaskState.Canceled)
            {
                return false;
            }

            task.PauseRequested = false;
            task.State = DownloadTaskState.Canceled;
            task.Cancellation?.Cancel();
        }

        Emit();

        return true;
    }

    /// <summary>从列表里移除（只允许已结束的任务）。</summary>
    public bool Remove(string id)
    {
        lock (_gate)
        {
            DownloadTask? task = _tasks.FirstOrDefault(t => t.Id == id);

            if (task is null || task.State is DownloadTaskState.Running or DownloadTaskState.Queued or DownloadTaskState.Paused)
            {
                return false;
            }

            _tasks.Remove(task);
        }

        Emit();

        return true;
    }

    /// <summary>
    /// 等某个任务结束（成功返回 true）。启动前补齐用：补齐了才继续起游戏。
    /// </summary>
    public async Task<bool> WaitForAsync(string id, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            DownloadTaskState? state;

            lock (_gate)
            {
                state = _tasks.FirstOrDefault(task => task.Id == id)?.State;
            }

            if (state is null)
            {
                return false;
            }

            if (state is DownloadTaskState.Done)
            {
                return true;
            }

            if (state is DownloadTaskState.Failed or DownloadTaskState.Canceled)
            {
                return false;
            }

            await Task.Delay(120, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            DownloadTask? next;

            lock (_gate)
            {
                if (Volatile.Read(ref _running) >= MaxParallelTasks)
                {
                    return;
                }

                next = _tasks.FirstOrDefault(task => task.State == DownloadTaskState.Queued);

                if (next is null)
                {
                    return;
                }

                next.State = DownloadTaskState.Running;
                Interlocked.Increment(ref _running);
            }

            Emit();
            _ = Task.Run(() => RunTaskAsync(next));
        }
    }

    private async Task RunTaskAsync(DownloadTask task)
    {
        using var cancellation = new CancellationTokenSource();
        task.Cancellation = cancellation;

        try
        {
            // 任务内并发：总线程预算按"同时在跑的任务数"摊，避免 8 个任务各开 64 条连接
            int parallelism = Math.Max(1, ThreadsPerTask / Math.Max(1, MaxParallelTasks));

            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = parallelism,
                CancellationToken = cancellation.Token
            };

            await Parallel.ForEachAsync(task.Items, options, async (item, token) =>
            {
                DownloadResult result = await _downloader
                    .DownloadAsync(item, maxAttempts: 3, cancellationToken: token, onBytes: task.AddBytes)
                    .ConfigureAwait(false);

                // 只有真的下过才算"完成一个文件"。已经存在且校验通过的那些，
                // 字节和文件数在建任务时就已经预置进来了，再记一次会让进度变成 8732/4366。
                if (result.Outcome == DownloadOutcome.Downloaded)
                {
                    task.MarkFileDone();
                }
            }).ConfigureAwait(false);

            task.State = DownloadTaskState.Done;
        }
        catch (OperationCanceledException) when (task.PauseRequested)
        {
            task.State = DownloadTaskState.Paused;
        }
        catch (OperationCanceledException)
        {
            task.State = DownloadTaskState.Canceled;
        }
        catch (Exception ex)
        {
            task.State = DownloadTaskState.Failed;
            task.Error = ex.Message;
        }
        finally
        {
            task.Cancellation = null;
            Interlocked.Decrement(ref _running);

            Emit();
            _ = Task.Run(PumpAsync);
        }
    }

    private void EmitProgress()
    {
        lock (_gate)
        {
            if (_tasks.All(task => task.State is not DownloadTaskState.Running))
            {
                return;
            }
        }

        Emit();
    }

    private void Emit()
    {
        DownloadTaskSnapshot[] snapshot = [.. Snapshot()];

        _emit(snapshot);
        Changed?.Invoke(snapshot);
    }

    public ValueTask DisposeAsync()
    {
        _progressTimer.Dispose();
        _http.Dispose();

        return ValueTask.CompletedTask;
    }
}
