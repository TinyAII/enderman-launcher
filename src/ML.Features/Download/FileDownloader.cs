using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

// SHA1 在这里不是安全哈希，是 Minecraft 资源清单里写死的校验算法，没有选择余地。
#pragma warning disable CA5350

namespace ML.Features.Download;

/// <summary>
/// 单文件下载器。做四件事，每件都有实测覆盖：
/// <list type="number">
/// <item>先校验再下载——本地文件哈希对得上就一个请求都不发。</item>
/// <item>断点续传——用 <c>.part</c> 文件 + <c>Range</c> 请求，服务器不支持时自动退回重下。</item>
/// <item>边下边算哈希——不在下完之后再读一遍文件。</item>
/// <item>原子落盘——校验通过才改名到最终路径，失败绝不留下半截文件被误认完好。</item>
/// </list>
/// </summary>
public sealed class FileDownloader(HttpClient http, RateLimiter? limiter = null)
{
    /// <summary>临时文件后缀。<c>.part</c> 留在磁盘上就代表「没下完」。</summary>
    public const string PartSuffix = ".part";

    private const int BufferSize = 64 * 1024;

    public HttpClient Http { get; } = http;

    /// <summary>下载一个文件，失败按指数退避重试。</summary>
    public async Task<DownloadResult> DownloadAsync(
        DownloadItem item,
        int maxAttempts = 3,
        CancellationToken cancellationToken = default,
        Action<long>? onBytes = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        if (await IsAlreadyValidAsync(item, cancellationToken).ConfigureAwait(false))
        {
            return new DownloadResult
            {
                Item = item,
                Outcome = DownloadOutcome.AlreadyValid,
                Bytes = new FileInfo(item.DestinationPath).Length
            };
        }

        string? lastError = null;
        var lastOutcome = DownloadOutcome.Failed;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var bytes = await TransferAsync(item, cancellationToken, onBytes).ConfigureAwait(false);

                return new DownloadResult
                {
                    Item = item,
                    Outcome = DownloadOutcome.Downloaded,
                    Attempts = attempt,
                    Bytes = bytes
                };
            }
            catch (ChecksumMismatchException ex)
            {
                // 哈希不对说明拿到的字节是坏的，续传只会继续坏下去，必须丢掉重下。
                lastError = ex.Message;
                lastOutcome = DownloadOutcome.HashMismatch;
                TryDelete(PartPath(item));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                // 网络/磁盘错误：保留 .part，下一次尝试从断点继续。
                lastError = ex.Message;
                lastOutcome = DownloadOutcome.Failed;
            }

            if (attempt < maxAttempts)
            {
                var backoff = TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1));
                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            }
        }

        return new DownloadResult
        {
            Item = item,
            Outcome = lastOutcome,
            Attempts = maxAttempts,
            Error = lastError
        };
    }

    /// <summary>临时文件路径。同目录同名前缀，保证 <see cref="File.Move(string, string, bool)"/> 不跨卷。</summary>
    public static string PartPath(DownloadItem item) => item.DestinationPath + PartSuffix;

    private async Task<long> TransferAsync(DownloadItem item, CancellationToken cancellationToken, Action<long>? onBytes)
    {
        var destination = item.DestinationPath;
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var part = PartPath(item);
        var existing = File.Exists(part) ? new FileInfo(part).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, item.Url);
        if (existing > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
        }

        using var response = await Http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
        {
            throw new HttpRequestException(
                $"服务器返回 {(int)response.StatusCode} {response.ReasonPhrase}",
                inner: null,
                response.StatusCode);
        }

        // 只有真的拿到 206 才算续传。服务器忽略 Range 返回 200 时，已落盘的半截必须丢弃。
        var resume = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!resume && File.Exists(part))
        {
            File.Delete(part);
            existing = 0;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);

        // 续传时要先把已落盘的前缀喂进哈希，否则算出来的永远是后缀的哈希。
        if (resume)
        {
            await AppendExistingToHashAsync(part, hash, cancellationToken).ConfigureAwait(false);
        }

        var total = existing;

        await using (var file = new FileStream(
                         part,
                         resume ? FileMode.Append : FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         BufferSize,
                         FileOptions.Asynchronous))
        await using (var body = await response.Content
                         .ReadAsStreamAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            var buffer = new byte[BufferSize];
            int read;
            while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                // 限速在写盘之前：这样"读出来的数据"和"落盘的进度"是同一个节奏，
                // 界面上的速度不会因为缓冲区攒数据而虚高。
                if (limiter is not null)
                {
                    await limiter.WaitAsync(read, cancellationToken).ConfigureAwait(false);
                }

                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                total += read;
                onBytes?.Invoke(read);
            }
        }

        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());

        if (item.Size is { } expectedSize && total != expectedSize)
        {
            throw new ChecksumMismatchException($"大小不符：期望 {expectedSize} 字节，实际 {total} 字节");
        }

        if (!string.IsNullOrEmpty(item.Sha1) &&
            !string.Equals(actual, item.Sha1.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new ChecksumMismatchException($"SHA1 不符：期望 {item.Sha1}，实际 {actual}");
        }

        File.Move(part, destination, overwrite: true);
        return total;
    }

    private static async Task AppendExistingToHashAsync(
        string path,
        IncrementalHash hash,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous);

        var buffer = new byte[BufferSize];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }
    }

    private static async Task<bool> IsAlreadyValidAsync(
        DownloadItem item,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(item.DestinationPath))
        {
            return false;
        }

        var length = new FileInfo(item.DestinationPath).Length;
        if (item.Size is { } expectedSize && length != expectedSize)
        {
            return false;
        }

        if (string.IsNullOrEmpty(item.Sha1))
        {
            // 既没哈希也没大小 → 无法判定，宁可重下也不信任来路不明的文件。
            return item.Size is not null;
        }

        try
        {
            await using var stream = File.OpenRead(item.DestinationPath);
            var actual = Convert.ToHexStringLower(
                await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));

            return string.Equals(actual, item.Sha1.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 删不掉不影响重试：下一次 TransferAsync 会按 Create 模式覆盖。
        }
    }

    /// <summary>校验失败。用异常是为了让重试循环统一处理「哈希不对」和「大小不对」。</summary>
    private sealed class ChecksumMismatchException(string message) : Exception(message);
}
