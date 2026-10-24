namespace ML.Host.Accounts;

/// <summary>
/// 内置的微软应用（客户端）ID。
/// </summary>
/// <remarks>
/// <para>
/// 每个第三方启动器都要在 Azure 注册一个应用，拿一个 client_id 才能走设备码登录——
/// 这个 ID 代表「是哪个程序在请求登录」，不是玩家的身份。PCL 之类之所以不用用户填，
/// 就是把它编进了程序里。
/// </para>
/// <para>
/// 注册一次（portal.azure.com → 应用注册 → 账户类型「个人 Microsoft 账户」→ 允许公共客户端流），
/// 把「应用程序(客户端) ID」填在这里，用户就再也不需要看见任何 ID 输入框。
/// </para>
/// <para>
/// 留空 = 没有内置，界面会退回让用户自己填；环境变量 <c>ML_MS_CLIENT_ID</c> 优先级高于这里，
/// 设置页里手填的值优先级最高（自建应用的用户要能覆盖）。
/// </para>
/// </remarks>
internal static class AccountDefaults
{
    /// <summary>
    /// Enderman Launcher 的应用程序(客户端) ID（2026-10-22 注册，账户类型「所有 Microsoft 帐户用户」）。
    /// 授权页会显示应用名，所以这个名字就是玩家看到的名字。
    /// </summary>
    public const string MicrosoftClientId = "b9a48221-f0cb-41c1-abc1-b730d4c4a5b4";
}
