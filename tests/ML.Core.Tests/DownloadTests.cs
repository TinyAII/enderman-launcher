using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using ML.Features.Download;

// 测试里算的 SHA1 是为了跟 Minecraft 资源清单对齐，不是安全用途。
#pragma warning disable CA5350

namespace ML.Core.Tests;

/// <summary>下载引擎测试。全部打真实 HTTP 请求，不打桩——协议细节只有真连接才暴露得出来。</summary>
public class DownloadTests
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("ML download engine fixture payload 0123456789");

    private static string Sha1Of(byte[] bytes) => Convert.ToHexStringLower(SHA1.HashData(bytes));

    private static FileDownloader NewDownloader() => new(new HttpClient());

    private static Task ServeBytes(HttpContext context, byte[] payload)
    {
        context.Response.ContentLength = payload.Length;
        return context.Response.Body.WriteAsync(payload).AsTask();
    }

    // ── 基本路径 ──────────────────────────────────────────────────

    [Fact]
    public async Task 下载文件并校验sha1()
    {
        await using var server = await TestServer.StartAsync(ctx => ServeBytes(ctx, Payload));
        using var temp = new TempDirectory();

        var destination = temp.Path("a.jar");
        var result = await NewDownloader().DownloadAsync(new DownloadItem
        {
            Url = server.Url("/a.jar").ToString(),
            DestinationPath = destination,
            Sha1 = Sha1Of(Payload),
            Size = Payload.Length
        });

        Assert.Equal(DownloadOutcome.Downloaded, result.Outcome);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(Payload.Length, result.Bytes);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(destination + FileDownloader.PartSuffix), "不该留下 .part");
    }

    [Fact]
    public async Task 已存在的正确文件一个请求都不发()
    {
        await using var server = await TestServer.StartAsync(ctx => ServeBytes(ctx, Payload));
        using var temp = new TempDirectory();

        var destination = temp.Path("a.jar");
        await File.WriteAllBytesAsync(destination, Payload);

        var result = await NewDownloader().DownloadAsync(new DownloadItem
        {
            Url = server.Url("/a.jar").ToString(),
            DestinationPath = destination,
            Sha1 = Sha1Of(Payload)
        });

        Assert.Equal(DownloadOutcome.AlreadyValid, result.Outcome);
        Assert.Equal(0, result.Attempts);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task 已存在的坏文件会被重下覆盖()
    {
        await using var server = await TestServer.StartAsync(ctx => ServeBytes(ctx, Payload));
        using var temp = new TempDirectory();

        var destination = temp.Path("a.jar");
        await File.WriteAllBytesAsync(destination, Encoding.UTF8.GetBytes("garbage"));

        var result = await NewDownloader().DownloadAsync(new DownloadItem
        {
            Url = server.Url("/a.jar").ToString(),
            DestinationPath = destination,
            Sha1 = Sha1Of(Payload)
        });

        Assert.Equal(DownloadOutcome.Downloaded, result.Outcome);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task 目标目录不存在时自动创建()
    {
        await using var server = await TestServer.StartAsync(ctx => ServeBytes(ctx, Payload));
        using var temp = new TempDirectory();

        var destination = temp.Path(Path.Combine("deep", "nested", "dir", "a.jar"));
        var result = await NewDownloader().DownloadAsync(new DownloadItem
        {
            Url = server.Url("/a.jar").ToString(),
            DestinationPath = destination,
            Sha1 = Sha1Of(Payload)
        });

        Assert.Equal(DownloadOutcome.Downloaded, result.Outcome);
        Assert.True(File.Exists(destination));
    }

    // ── 失败路径 ──────────────────────────────────────────────────

    [Fact]
    public async Task 哈希不符报HashMismatch并在重试用尽后清掉part()
    {
        await using var server = await TestServer.StartAsync(ctx => ServeBytes(ctx, Payload));
        using var temp = new TempDirectory();

        var destination = temp.Path("a.jar");
        var result = await NewDownloader().DownloadAsync(new DownloadItem
        {
            Url = server.Url("/a.jar").ToString(),
            DestinationPath = destination,
            Sha1 = Sha1Of(Encoding.UTF8.GetBytes("完全不同的内容"))
        });

        Assert.Equal(DownloadOutcome.HashMismatch, result.Outcome);
        Assert.Equal(3, result.Attempts);
        Assert.NotNull(result.Error);
        Assert.Contains("SHA1", result.Error, StringComparison.Ordinal);

        Assert.False(File.Exists(destination), "坏内容不该落到最终路径");
        Assert.False(
            File.Exists(destination + FileDownloader.PartSuffix),
            "哈希错误后必须删掉 .part，否则下次续传还是坏字节");
    }

    [Fact]
    public async Task 大小不符也算校验失败()
    {
        await using var server = await TestServer.StartAsync(ctx => ServeBytes(ctx, Payload));
        using var temp = new TempDirectory();

        var destination = temp.Path("a.jar");
        var result = await NewDownloader().DownloadAsync(new DownloadItem
        {
            Url = server.Url("/a.jar").ToString(),
            DestinationPath = destination,
            Sha1 = Sha1Of(Payload),
            Size = Payload.Length + 1
        });

        Assert.Equal(DownloadOutcome.HashMismatch, result.Outcome);
        Assert.Contains("大小不符", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 服务器404会重试三次再报Failed()
    {
        await using var server = await TestServer.StartAsync(ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });
        using var temp = new TempDirectory();

        var destination = temp.Path("missing.jar");
        var result = await NewDownloader().DownloadAsync(new DownloadItem
        {
            Url = server.Url("/missing.jar").ToString(),
            DestinationPath = destination,
            Sha1 = Sha1Of(Payload)
        }, maxAttempts: 3);

        Assert.Equal(DownloadOutcome.Failed, result.Outcome);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(3, server.RequestCount);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task 取消会抛出而不是静默返回失败()
    {
        await using var server = await TestServer.StartAsync(ctx => ServeBytes(ctx, Payload));
        using var temp = new TempDirectory();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NewDownloader().DownloadAsync(
                new DownloadItem
                {
                    Url = server.Url("/a.jar").ToString(),
                    DestinationPath = temp.Path("a.jar"),
                    Sha1 = Sha1Of(Payload)
                },
                cancellationToken: cts.Token));
    }

    // ── 断点续传 ──────────────────────────────────────────────────

    [Fact]
    public async Task 断点续传只请求剩下的字节()
    {
        await using var server = await TestServer.StartAsync(ctx => ServeRange(ctx, Payload));
        using var temp = new TempDirectory();

        var destination = temp.Path("a.jar");
        var prefix = Payload[..10];
        await File.WriteAllBytesAsync(destination + FileDownloader.PartSuffix, prefix);

        var result = await NewDownloader().DownloadAsync(new DownloadItem
        {
            Url = server.Url("/a.jar").ToString(),
            DestinationPath = destination,
            Sha1 = Sha1Of(Payload),
            Size = Payload.Length
        });

        Assert.Equal(DownloadOutcome.Downloaded, result.Outcome);
        Assert.Equal(Payload.Length, result.Bytes);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(destination));
        Assert.Contains("bytes=10-", server.RequestedRanges[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task 服务器不支持Range时退回全量重下()
    {
        // 服务器忽略 Range 一律返回 200：已落盘的半截必须丢掉，否则拼出来的是坏文件。
        await using var server = await TestServer.StartAsync(ctx => ServeBytes(ctx, Payload));
        using var temp = new TempDirectory();

        var destination = temp.Path("a.jar");
        await File.WriteAllBytesAsync(
            destination + FileDownloader.PartSuffix,
            Encoding.UTF8.GetBytes("stale-partial"));

        var result = await NewDownloader().DownloadAsync(new DownloadItem
        {
            Url = server.Url("/a.jar").ToString(),
            DestinationPath = destination,
            Sha1 = Sha1Of(Payload),
            Size = Payload.Length
        });

        Assert.Equal(DownloadOutcome.Downloaded, result.Outcome);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task 续传时哈希覆盖了已落盘的前缀()
    {
        // 这条是续传最容易写错的地方：只对后缀算哈希，得到的永远是错的。
        await using var server = await TestServer.StartAsync(ctx => ServeRange(ctx, Payload));
        using var temp = new TempDirectory();

        var destination = temp.Path("a.jar");
        await File.WriteAllBytesAsync(
            destination + FileDownloader.PartSuffix,
            Payload[..(Payload.Length - 1)]);

        var result = await NewDownloader().DownloadAsync(new DownloadItem
        {
            Url = server.Url("/a.jar").ToString(),
            DestinationPath = destination,
            Sha1 = Sha1Of(Payload)
        });

        // 只补最后 1 个字节，哈希仍然要对得上——否则说明前缀没进哈希
        Assert.Equal(DownloadOutcome.Downloaded, result.Outcome);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(destination));
    }

    // ── 队列 ──────────────────────────────────────────────────────

    [Fact]
    public async Task 队列按落盘路径去重()
    {
        await using var server = await TestServer.StartAsync(ctx => ServeBytes(ctx, Payload));
        using var temp = new TempDirectory();

        var destination = temp.Path("a.jar");
        var url = server.Url("/a.jar").ToString();

        var report = await new DownloadQueue(NewDownloader()).RunAsync(
            [
                new DownloadItem { Url = url, DestinationPath = destination, Sha1 = Sha1Of(Payload) },
                new DownloadItem { Url = url + "?dup=1", DestinationPath = destination, Sha1 = Sha1Of(Payload) }
            ],
            concurrency: 4);

        Assert.Single(report.Results);
        Assert.True(report.AllSucceeded);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task 队列并发上限被遵守()
    {
        var concurrent = 0;
        var peak = 0;

        await using var server = await TestServer.StartAsync(async ctx =>
        {
            var current = Interlocked.Increment(ref concurrent);

            int observed;
            while (current > (observed = Volatile.Read(ref peak)) &&
                   Interlocked.CompareExchange(ref peak, current, observed) != observed)
            {
            }

            await Task.Delay(40);
            Interlocked.Decrement(ref concurrent);
            await ServeBytes(ctx, Payload);
        });

        using var temp = new TempDirectory();
        var items = Enumerable.Range(0, 6).Select(i => new DownloadItem
        {
            Url = server.Url($"/{i}.jar").ToString(),
            DestinationPath = temp.Path($"{i}.jar"),
            Sha1 = Sha1Of(Payload)
        });

        var report = await new DownloadQueue(NewDownloader()).RunAsync(items, concurrency: 2);

        Assert.Equal(6, report.Total);
        Assert.True(report.AllSucceeded);
        Assert.True(peak <= 2, $"并发峰值 {peak} 超过了上限 2");
        Assert.True(peak >= 2, $"并发峰值只有 {peak}，上限没被真正用上");
    }

    [Fact]
    public async Task 队列汇总能区分成功与失败()
    {
        await using var server = await TestServer.StartAsync(ctx =>
            ctx.Request.Path.Value!.EndsWith("bad.jar", StringComparison.Ordinal)
                ? BadStatus(ctx)
                : ServeBytes(ctx, Payload));

        using var temp = new TempDirectory();
        var report = await new DownloadQueue(NewDownloader()).RunAsync(
            [
                new DownloadItem
                {
                    Url = server.Url("/good.jar").ToString(),
                    DestinationPath = temp.Path("good.jar"),
                    Sha1 = Sha1Of(Payload)
                },
                new DownloadItem
                {
                    Url = server.Url("/bad.jar").ToString(),
                    DestinationPath = temp.Path("bad.jar"),
                    Sha1 = Sha1Of(Payload)
                }
            ],
            concurrency: 2);

        Assert.Equal(2, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Single(report.Failures);
        Assert.False(report.AllSucceeded);
        Assert.EndsWith("bad.jar", report.Failures[0].Item.DestinationPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 进度回报次数等于任务数()
    {
        await using var server = await TestServer.StartAsync(ctx => ServeBytes(ctx, Payload));
        using var temp = new TempDirectory();

        // 实测踩到过：用 Progress<T> 收集会漏报。
        // Progress<T>.Report 是 Post 到同步上下文的异步投递，测试断言会在回调落袋之前先跑，
        // 于是 4 条任务只看到 3 条回报。这测的是 BCL 的调度，不是我们的队列，
        // 所以这里用同步实现的收集器。
        var progress = new SyncProgress<DownloadProgress>();

        var items = Enumerable.Range(0, 4).Select(i => new DownloadItem
        {
            Url = server.Url($"/{i}.jar").ToString(),
            DestinationPath = temp.Path($"{i}.jar"),
            Sha1 = Sha1Of(Payload)
        });

        var report = await new DownloadQueue(NewDownloader())
            .RunAsync(items, concurrency: 2, progress: progress);

        Assert.Equal(4, progress.Items.Count);
        Assert.Equal(4, progress.Items.Max(r => r.Completed));
        Assert.All(progress.Items, r => Assert.Equal(4, r.Total));
        Assert.True(report.AllSucceeded);
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly List<T> _items = [];

        public IReadOnlyList<T> Items
        {
            get
            {
                lock (_items)
                {
                    return _items.ToArray();
                }
            }
        }

        public void Report(T value)
        {
            lock (_items)
            {
                _items.Add(value);
            }
        }
    }

    // ── 辅助 ──────────────────────────────────────────────────────

    private static Task BadStatus(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return Task.CompletedTask;
    }

    /// <summary>支持 <c>Range</c> 的服务器：真发 206 + 切片。</summary>
    private static async Task ServeRange(HttpContext context, byte[] payload)
    {
        var range = context.Request.Headers.Range.ToString();

        if (range.StartsWith("bytes=", StringComparison.Ordinal))
        {
            var spec = range["bytes=".Length..];
            var dash = spec.IndexOf('-', StringComparison.Ordinal);
            var start = int.Parse(
                dash >= 0 ? spec[..dash] : spec,
                System.Globalization.CultureInfo.InvariantCulture);

            var slice = payload.AsMemory(start);
            context.Response.StatusCode = StatusCodes.Status206PartialContent;
            context.Response.ContentLength = slice.Length;
            await context.Response.Body.WriteAsync(slice);
            return;
        }

        context.Response.ContentLength = payload.Length;
        await context.Response.Body.WriteAsync(payload);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            RootPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "ml-dl-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
        }

        private string RootPath { get; }

        public string Path(string relative) =>
            System.IO.Path.Combine(RootPath, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootPath))
                {
                    Directory.Delete(RootPath, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
