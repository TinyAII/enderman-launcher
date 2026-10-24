using ML.Host.Shell;
using ML.Plugin.Abstractions;

namespace ML.Host.Plugins;

/// <summary>
/// <see cref="IHostServices"/> 的宿主实现：把实例、版本、账号翻成插件认识的形状。
/// </summary>
/// <remarks>
/// 这一层是**边界**：插件拿到的都是纯数据（record），拿不到 <c>Instance</c>、<c>GamePaths</c>
/// 这些内核类型。以后要改内部模型，只要这里的映射跟着改，插件不用重编。
/// </remarks>
internal sealed class HostServices : IHostServices
{
    private readonly Shell.ShellHost _shell;

    public HostServices(Shell.ShellHost shell)
    {
        _shell = shell;
    }

    public string GameRoot => _shell.Paths.Root;

    public IVersionInstallService Installer => new HostInstaller(_shell);

    public IDownloadService Downloads => new HostDownloads(_shell);

    public string ServerRoot => _shell.ServerRootPath;

    public bool AdoptProcess(int processId) => _shell.AdoptProcess(processId);

    public IReadOnlyList<HostInstance> Instances =>
        _shell.InstanceStore.List()
            .Select(instance => new HostInstance(
                instance.Metadata.Name,
                instance.Metadata.VersionId,
                instance.Directory,
                instance.GameDirectory,
                Path.Combine(instance.GameDirectory, "mods"),
                instance.Metadata.AccountId,
                _shell.IsRunning(instance.Metadata.Name)))
            .ToList();

    public IReadOnlyList<HostVersion> Versions =>
        _shell.InstalledVersions()
            .Select(version => new HostVersion(version.Id, version.Type, version.Protocol))
            .ToList();

    public HostAccount? CurrentAccount
    {
        get
        {
            var account = _shell.CurrentAccountView();
            return account is null ? null : new HostAccount(account.Name, account.Kind, account.Uuid ?? string.Empty);
        }
    }

    public Task<HostLaunchResult> LaunchAsync(string instanceName, CancellationToken cancellationToken = default) =>
        _shell.LaunchForPluginAsync(instanceName, cancellationToken);
}


/// <summary>把宿主的安装能力包成契约接口。</summary>
internal sealed class HostInstaller(ShellHost shell) : IVersionInstallService
{
    public Task<IReadOnlyList<HostRemoteVersion>> ListRemoteAsync(
        bool includeSnapshots,
        CancellationToken cancellationToken = default) =>
        shell.ListRemoteVersionsAsync(includeSnapshots, cancellationToken);

    public Task<HostInstallResult> InstallAsync(
        string versionId,
        IProgress<HostInstallProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        shell.InstallVersionAsync(versionId, progress, cancellationToken);
}


/// <summary>把宿主的下载队列包成契约接口。</summary>
internal sealed class HostDownloads(ShellHost shell) : IDownloadService
{
    public string? Submit(string title, IReadOnlyList<HostDownloadFile> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(files);

        return shell.SubmitDownload(
            title,
            [.. files.Select(file => new ML.Features.Download.DownloadItem
            {
                Url = file.Url,
                DestinationPath = file.TargetPath,
                Sha1 = file.Sha1,
                Size = file.Size
            })]);
    }

    public Task<bool> WaitAsync(string taskId, CancellationToken cancellationToken = default) =>
        shell.WaitForDownloadAsync(taskId, cancellationToken);

    public HostDownloadStatus? Status(string taskId) => shell.DownloadStatus(taskId);
}
