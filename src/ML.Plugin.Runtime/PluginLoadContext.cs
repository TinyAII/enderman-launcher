using System.Reflection;
using System.Runtime.Loader;
using ML.Plugin.Abstractions;

namespace ML.Plugin.Runtime;

/// <summary>
/// 每个插件一个独立的加载上下文（Tier2 隔离的地基）。
/// </summary>
/// <remarks>
/// 关键一条：契约程序集（ML.Plugin.Abstractions）必须**回落到默认上下文**。
/// 如果插件目录里也带了一份，它加载出来的 <c>IPlugin</c> 和宿主手里的就是两个不同的类型，
/// 强转必然失败——这是 .NET 插件系统最常见的坑，症状还特别像"插件没实现接口"。
/// </remarks>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly string ContractAssembly =
        typeof(IPlugin).Assembly.GetName().Name ?? "ML.Plugin.Abstractions";

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string entryPath)
        : base($"plugin:{Path.GetFileNameWithoutExtension(entryPath)}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(entryPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(assemblyName.Name, ContractAssembly, StringComparison.OrdinalIgnoreCase))
        {
            // 返回 null = 交给默认上下文，拿到宿主那份契约类型。
            return null;
        }

        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}

/// <summary>一个已经装好的插件实例。</summary>
internal sealed class LoadedPlugin
{
    public required PluginManifest Manifest { get; init; }

    public PluginLoadContext? Context { get; init; }

    public IPlugin? Instance { get; init; }

    public ILaunchHook? LaunchHook { get; init; }

    /// <summary>启动前守卫：能拒绝一次启动（<see cref="ILaunchHook"/> 只能加参数）。</summary>
    public ILaunchGuard? LaunchGuard { get; init; }

    public IPluginCommand? Command { get; init; }

    public IEventHook? Events { get; init; }
}
