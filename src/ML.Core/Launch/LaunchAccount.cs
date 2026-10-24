using System.Security.Cryptography;
using System.Text;

namespace ML.Core.Launch;

/// <summary>
/// 启动账号。正版与离线共用这一个形状——启动参数里它们的区别只有
/// <c>accessToken</c> 与 <c>userType</c> 的取值，分成两个类型会让参数装配出现分支。
/// </summary>
public sealed record LaunchAccount
{
    /// <summary>玩家名。正版是账号名，离线是自己起的名字。</summary>
    public required string PlayerName { get; init; }

    /// <summary>32 位无连字符小写十六进制。</summary>
    public required string Uuid { get; init; }

    /// <summary>离线模式填 <c>0</c>：服务端认证失败是预期的，填什么都一样。</summary>
    public string AccessToken { get; init; } = "0";

    /// <summary>离线是 <c>legacy</c>，正版是 <c>msa</c>。</summary>
    public string UserType { get; init; } = "legacy";

    public string? Xuid { get; init; }

    public string? ClientId { get; init; }

    public bool IsOffline => string.Equals(UserType, "legacy", StringComparison.Ordinal);
}

/// <summary>
/// 离线账号。<c>uuid</c> 的算法是社区事实标准：<c>MD5("OfflinePlayer:" + 玩家名)</c>，
/// 再把哈希改成 RFC 4122 version 3 的形态。
/// </summary>
/// <remarks>
/// 这个算法不是 Mojang 公布的，但被所有第三方启动器采用，且服务端（含 Bukkit/Spigot 系）
/// 用它判断"同一个离线名 = 同一个玩家数据目录"。改动它就等于让所有离线存档对不上号，
/// 所以哪怕 MD5 本身不安全也不能换。这里 SHA1 之类的"更安全"替换是错的。
/// </remarks>
public static class OfflineAccount
{
    public const string UuidPrefix = "OfflinePlayer:";

    /// <summary>Minecraft 协议里玩家名的长度上限。</summary>
    public const int MaxNameLength = 16;

    public static LaunchAccount Create(string playerName)
    {
        ValidateName(playerName);
        return new LaunchAccount
        {
            PlayerName = playerName,
            Uuid = ComputeUuid(playerName),
        };
    }

#pragma warning disable CA5351 // MD5 是这里唯一正确的选择，见类型注释
    public static string ComputeUuid(string playerName)
    {
        ValidateName(playerName);

        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(UuidPrefix + playerName));

        // RFC 4122 version 3：第 7 字节高 4 位置 0x3，第 9 字节高 2 位置 0b10。
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        return Convert.ToHexStringLower(hash);
    }
#pragma warning restore CA5351

    private static void ValidateName(string playerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerName);

        if (playerName.Length > MaxNameLength)
        {
            throw new ArgumentException(
                $"玩家名最长 {MaxNameLength} 个字符，实际 {playerName.Length} 个：{playerName}",
                nameof(playerName));
        }

        foreach (char c in playerName)
        {
            // 名字会被直接塞进命令行参数；空白与控制字符在这里挡掉，
            // 比在装配参数时再转义可靠。
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                throw new ArgumentException(
                    "玩家名不能包含空白或控制字符。",
                    nameof(playerName));
            }
        }
    }
}
