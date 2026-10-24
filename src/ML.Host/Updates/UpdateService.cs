using Velopack;

namespace ML.Host.Updates;

/// <summary>一次更新检查的结论。<see cref="Message"/> 是给人看的一句话，不是错误码。</summary>
internal sealed record UpdateStatus(
    bool Checked,
    bool Available,
    string CurrentVersion,
    string? NewVersion,
    bool CanApply,
    string Message);

/// <summary>
/// 更新检查。用 Velopack：它是 .NET 圈里现在唯一把「安装包 + 差分更新 + 自替换」做全的方案
/// （Squirrel 的继任者），安装包与更新包由同一套 <c>vpk pack</c> 产出，不需要自己写引导器去替换运行中的 exe。
/// </summary>
/// <remarks>
/// 三件必须说清的事：
/// ① **更新源不写死**。地址从 <c>ML_UPDATE_URL</c> 读，支持 <c>https://…</c> 静态目录、<c>file://</c>
///    本地目录、GitHub Releases（<c>gh:</c> 前缀）。没配就明确回「未配置更新源」，而不是假装检查过了。
/// ② **不是安装形态就不检查**。开发期 <c>dotnet run</c> 出来的东西没有被 Velopack 装过，
///    强行检查只会拿到一条 NotInstalled 异常，界面上就是一句看不懂的英文。
/// ③ 下载与替换交给 Velopack：它会另起进程、等本进程退出后再改文件，这正是我们手写会写错的地方。
/// </remarks>
internal static class UpdateService
{
    private const string FeedUrlVariable = "ML_UPDATE_URL";

    /// <summary>
    /// 编译期默认更新源。留空 = 还没服务器，靠 <c>ML_UPDATE_URL</c> 临时指定。
    /// 有服务器之后把静态目录地址填在这里（也可以改成从配置读），用户就不必设环境变量。
    /// </summary>
    private const string DefaultFeedUrl = "";

    public static string CurrentVersion { get; } =
        typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static string? FeedUrl
    {
        get
        {
            string? value = Environment.GetEnvironmentVariable(FeedUrlVariable);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }

            return string.IsNullOrWhiteSpace(DefaultFeedUrl) ? null : DefaultFeedUrl;
        }
    }

    public static string FeedDescription => FeedUrl ?? $"未配置（环境变量 {FeedUrlVariable}）";

    public static async Task<UpdateStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        string? feed = FeedUrl;
        if (feed is null)
        {
            return new UpdateStatus(false, false, CurrentVersion, null, false, "未配置更新源。");
        }

        UpdateManager manager;
        try
        {
            manager = new UpdateManager(feed);
        }
        catch (Exception ex)
        {
            return new UpdateStatus(false, false, CurrentVersion, null, false, $"更新源不可用：{ex.Message}");
        }

        if (!manager.IsInstalled)
        {
            return new UpdateStatus(false, false, manager.CurrentVersion?.ToString() ?? CurrentVersion, null, false,
                "当前不是通过安装包运行的（便携版或开发运行），跳过更新检查。");
        }

        try
        {
            UpdateInfo? info = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            string current = manager.CurrentVersion?.ToString() ?? CurrentVersion;

            if (info is null)
            {
                return new UpdateStatus(true, false, current, null, false, "已是最新版本。");
            }

            string target = info.TargetFullRelease.Version.ToString();
            return new UpdateStatus(true, true, current, target, true, $"发现新版本 {target}。");
        }
        catch (Exception ex)
        {
            return new UpdateStatus(false, false, CurrentVersion, null, false, $"检查失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>下载并替换。成功的话本进程会退出，由 Velopack 的更新器接管并重新拉起新版本。</summary>
    public static async Task<UpdateStatus> ApplyAsync(
        Action<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string? feed = FeedUrl;
        if (feed is null)
        {
            return new UpdateStatus(false, false, CurrentVersion, null, false, "未配置更新源。");
        }

        var manager = new UpdateManager(feed);
        if (!manager.IsInstalled)
        {
            return new UpdateStatus(false, false, CurrentVersion, null, false, "当前不是通过安装包运行的，无法应用更新。");
        }

        try
        {
            UpdateInfo? info = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null)
            {
                return new UpdateStatus(true, false, manager.CurrentVersion?.ToString() ?? CurrentVersion, null, false, "已是最新版本。");
            }

            await manager.DownloadUpdatesAsync(info, progress, cancellationToken).ConfigureAwait(false);
            string target = info.TargetFullRelease.Version.ToString();

            // 这一句之后不要再假设自己还在跑：它会起更新器、等本进程退出。
            manager.ApplyUpdatesAndRestart(info.TargetFullRelease, []);

            return new UpdateStatus(true, true, manager.CurrentVersion?.ToString() ?? CurrentVersion, target, true,
                $"已下载 {target}，正在重启完成更新。");
        }
        catch (Exception ex)
        {
            return new UpdateStatus(false, false, CurrentVersion, null, false, $"更新失败：{ex.GetType().Name}: {ex.Message}");
        }
    }
}
