using System.Text.Json;
using ML.Plugin.Abstractions;

namespace ML.Plugin.Vanilla;

/// <summary>
/// 官方原版安装器：从 Mojang 清单装版本。
/// </summary>
/// <remarks>
/// 它不自己实现下载——那是功能层的活。插件只负责"挑版本 + 触发安装 + 报告结果"，
/// 真正的库/客户端/资源下载走 <see cref="IHostServices.Installer"/>。
/// 这样插件和宿主不需要共享 <c>ML.Features</c> 的实例（插件程序集是隔离加载的）。
/// </remarks>
public sealed class VanillaPlugin : IPlugin, IPluginCommand
{
    private IPluginContext? _context;

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        context.Log.Info($"原版安装器就绪（游戏目录 {context.Host.GameRoot}）");

        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<string?> ExecuteAsync(string command, string? payload, CancellationToken cancellationToken)
    {
        if (_context is not { } context)
        {
            return Fail("插件还没初始化完。");
        }

        try
        {
            return command switch
            {
                "versions" => await ListVersionsAsync(context, cancellationToken).ConfigureAwait(false),
                "install" => await InstallAsync(context, payload, cancellationToken).ConfigureAwait(false),
                "download" => await DownloadAsync(context, payload, cancellationToken).ConfigureAwait(false),
                "installed" => Installed(context),
                _ => null
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            context.Log.Error($"执行 {command} 失败：{ex.Message}");

            return Fail(ex.Message);
        }
    }

    /// <summary>本机已装好的版本 id。页面用它把"已安装"标出来。</summary>
    private static string Installed(IPluginContext context) =>
        JsonSerializer.Serialize(new
        {
            ok = true,
            versions = context.Host.Versions.Select(version => version.Id).ToArray()
        });

    private static async Task<string> ListVersionsAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        IReadOnlyList<HostRemoteVersion> versions = await context.Host.Installer
            .ListRemoteAsync(includeSnapshots: false, cancellationToken)
            .ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            ok = true,
            count = versions.Count,
            versions = versions.Select(version => new
            {
                id = version.Id,
                type = version.Type,
                releaseTime = version.ReleaseTime
            })
        });
    }

    private static async Task<string> InstallAsync(
        IPluginContext context,
        string? payload,
        CancellationToken cancellationToken)
    {
        string? versionId = ReadVersionId(payload);

        if (string.IsNullOrWhiteSpace(versionId))
        {
            return Fail("缺少 versionId。");
        }

        context.Log.Info($"开始安装 {versionId}");

        var progress = new Progress<HostInstallProgress>(report =>
        {
            if (report.Total > 0)
            {
                context.Log.Info($"{report.Stage} {report.Completed}/{report.Total}（{report.BytesDone / 1024 / 1024} MB）");
            }
        });

        HostInstallResult result = await context.Host.Installer
            .InstallAsync(versionId, progress, cancellationToken)
            .ConfigureAwait(false);

        if (result.Ok)
        {
            context.Log.Info($"安装完成：{versionId}，下载 {result.FilesDownloaded} 个、跳过 {result.FilesSkipped} 个");
        }
        else
        {
            context.Log.Warn($"安装失败：{versionId} — {result.Error}");
        }

        return JsonSerializer.Serialize(new
        {
            ok = result.Ok,
            versionId = result.VersionId,
            error = result.Error,
            downloaded = result.FilesDownloaded,
            skipped = result.FilesSkipped,
            bytes = result.Bytes
        });
    }

    /// <summary>
    /// 演示插件怎么用下载队列：把 URL 丢进宿主的队列，就会出现在「下载」页。
    /// </summary>
    /// <remarks>
    /// 这条链路是给"资源插件"准备的：插件自己不用管并发、限速、断点续传、校验，
    /// 也不该自己 new HttpClient 去抢带宽——统一走队列，用户在下载页能看到、能暂停。
    /// </remarks>
    private static async Task<string> DownloadAsync(
        IPluginContext context,
        string? payload,
        CancellationToken cancellationToken)
    {
        (string? url, string? target) = ReadDownloadRequest(payload, context);

        if (string.IsNullOrWhiteSpace(url))
        {
            return Fail("缺少 url。");
        }

        string path = string.IsNullOrWhiteSpace(target)
            ? Path.Combine(context.DataDirectory, "downloads", SafeName(url))
            : target;

        string? taskId = context.Host.Downloads.Submit("插件下载", [
            new HostDownloadFile(url, path)
        ]);

        if (taskId is null)
        {
            return Fail($"下载队列已满（{500} 个任务）。");
        }

        context.Log.Info($"已提交下载：{url} → {path}（任务 {taskId}）");

        bool ok = await context.Host.Downloads.WaitAsync(taskId, cancellationToken).ConfigureAwait(false);
        HostDownloadStatus? status = context.Host.Downloads.Status(taskId);

        return JsonSerializer.Serialize(new
        {
            ok,
            taskId,
            path,
            state = status?.State,
            bytes = status?.DoneBytes ?? 0,
            error = status?.Error
        });
    }

    private static (string? Url, string? Target) ReadDownloadRequest(string? payload, IPluginContext context)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return (null, null);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;

            string? url = root.TryGetProperty("url", out JsonElement u) ? u.GetString() : null;
            string? target = root.TryGetProperty("path", out JsonElement p) ? p.GetString() : null;

            return (url, target);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>只留文件名部分，别让 URL 里的查询串和斜杠跑进路径。</summary>
    private static string SafeName(string url)
    {
        string name = Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            ? Path.GetFileName(uri.AbsolutePath)
            : "download.bin";

        if (string.IsNullOrWhiteSpace(name))
        {
            return "download.bin";
        }

        foreach (char bad in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(bad, '_');
        }

        return name;
    }

    private static string? ReadVersionId(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);

            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("versionId", out JsonElement value)
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Fail(string error) => JsonSerializer.Serialize(new { ok = false, error });
}
