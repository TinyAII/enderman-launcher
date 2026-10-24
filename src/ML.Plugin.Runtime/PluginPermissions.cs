namespace ML.Plugin.Runtime;

/// <summary>
/// 权限白名单与判定。
/// </summary>
/// <remarks>
/// <para>
/// <b>先说实话：同进程插件没有真沙箱。</b>Tier2 插件跑在宿主进程里，能直接反射到任何东西，
/// 任何"权限系统"都拦不住恶意代码。这里拦的是**宿主主动提供的入口**（目前就是启动参数），
/// 目的是让"这个插件改了启动参数"变成一件用户看得见、可以拒绝的事，而不是安全边界。
/// 真要隔离，得上 Tier3 子进程 + 操作系统级限制。
/// </para>
/// <para>
/// 未声明的权限一律不授予；不认识的名字只报警告（插件可能是为新版本写的）。
/// </para>
/// </remarks>
public static class PluginPermissions
{
    /// <summary>往 JVM 参数里追加东西（<c>-javaagent</c> 也在这条下面，所以必须单独同意）。</summary>
    public const string LaunchJvmArgs = "launch:jvm-args";

    /// <summary>往游戏参数里追加东西。</summary>
    public const string LaunchGameArgs = "launch:game-args";

    /// <summary>
    /// 已实现并会**真的执行**的权限。只有这些会被同意流程拦住。
    /// </summary>
    /// <summary>往界面插槽里挂 HTML / 脚本。注入的脚本能碰整个页面，所以必须同意。</summary>
    public const string UiPage = "ui:page";

    public static readonly IReadOnlyList<string> Enforced = [LaunchJvmArgs, LaunchGameArgs, UiPage];

    /// <summary>
    /// 契约里预留、但宿主还没实现的权限。写进清单不报错，也不会生效——
    /// 界面上会标出来，免得插件作者以为已经拿到手了。
    /// </summary>
    public static readonly IReadOnlyList<string> Reserved =
    [
        "network:https",
        "fs:instance",
        "account:token"
    ];

    public static bool IsEnforced(string permission) =>
        Enforced.Contains(permission, StringComparer.OrdinalIgnoreCase);

    public static bool IsReserved(string permission) =>
        Reserved.Contains(permission, StringComparer.OrdinalIgnoreCase);

    public static bool IsKnown(string permission) => IsEnforced(permission) || IsReserved(permission);

    /// <summary>清单里声明的权限中和"需要同意"相关的那些。</summary>
    public static IReadOnlyList<string> Required(IEnumerable<string> declared) =>
        declared.Where(IsEnforced).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
