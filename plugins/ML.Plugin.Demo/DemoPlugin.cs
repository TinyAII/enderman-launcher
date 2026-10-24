using ML.Plugin.Abstractions;

namespace ML.Plugin.Demo;

/// <summary>
/// 示例插件。演示插件能做的四类事：
/// 加载生命周期、改启动参数、响应宿主事件、被界面按钮调用并操作实例目录。
/// </summary>
public sealed class DemoPlugin : IPlugin, ILaunchHook, IPluginCommand, IEventHook
{
    private IPluginContext? _context;

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        context.Log.Info($"初始化完成，插件目录 {context.Directory}");
        context.Log.Info(
            $"宿主：游戏目录 {context.Host.GameRoot}，实例 {context.Host.Instances.Count} 个，"
            + $"版本 {context.Host.Versions.Count} 个");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        _context?.Log.Info("已卸载");
        return Task.CompletedTask;
    }

    public void OnBeforeLaunch(LaunchRequest request)
    {
        // 加一条无害的 JVM 参数：游戏不认它，但日志里能看见它，
        // 这样"钩子到底跑没跑"是可验证的，而不是靠相信。
        request.AddJvmArgument("-Dml.demo=1");

        _context?.Log.Info($"实例 {request.InstanceName}（{request.VersionId}，{request.PlayerName}）启动前被调用");
    }

    /// <summary>
    /// 页面按钮 → 插件代码：读宿主状态、真动手改实例目录。
    /// 下载器装完 mod 之后干的就是这类事。
    /// </summary>
    public Task<string?> ExecuteAsync(string command, string? payload, CancellationToken cancellationToken)
    {
        IPluginContext context = _context ?? throw new InvalidOperationException("插件还没初始化。");

        switch (command)
        {
            case "status":
                return Task.FromResult<string?>(
                    $"游戏目录 {context.Host.GameRoot}；实例 {context.Host.Instances.Count} 个；"
                    + $"版本 {context.Host.Versions.Count} 个；当前账号 {context.Host.CurrentAccount?.Name ?? "（无）"}");

            case "mods":
                return Task.FromResult<string?>(DescribeMods(context, payload));

            case "drop-marker":
            {
                HostInstance instance = FindInstance(context, payload);

                Directory.CreateDirectory(instance.ModsDirectory);
                string marker = Path.Combine(instance.ModsDirectory, "ml-demo-marker.jar");
                File.WriteAllText(marker, "示例插件写的占位文件，不是真的 mod。");

                context.Log.Info($"往 {instance.ModsDirectory} 写了一个占位文件");
                return Task.FromResult<string?>($"已写入 {marker}");
            }

            default:
                throw new InvalidOperationException($"不认识命令 {command}");
        }
    }

    /// <summary>宿主事件写到插件自己的数据目录，方便确认事件真的发出来了。</summary>
    public void OnHostEvent(string name, string? payloadJson)
    {
        IPluginContext? context = _context;
        if (context is null)
        {
            return;
        }

        try
        {
            File.AppendAllText(
                Path.Combine(context.DataDirectory, "events.log"),
                $"{DateTimeOffset.Now:HH:mm:ss}\t{name}\t{payloadJson}" + Environment.NewLine);
        }
        catch (IOException)
        {
            // 记不下就算了：事件不能反过来影响宿主。
        }
    }

    private static HostInstance FindInstance(IPluginContext context, string? name)
    {
        string target = name ?? string.Empty;

        return context.Host.Instances
                   .FirstOrDefault(i => string.Equals(i.Name, target, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException($"找不到实例「{target}」。");
    }

    private static string DescribeMods(IPluginContext context, string? name)
    {
        HostInstance instance = FindInstance(context, name);

        int count = Directory.Exists(instance.ModsDirectory)
            ? Directory.GetFiles(instance.ModsDirectory, "*.jar").Length
            : 0;

        return $"{instance.Name}（{instance.VersionId}）mods：{count} 个 jar · {instance.ModsDirectory}";
    }
}
