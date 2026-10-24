using System.Security.Cryptography;
using System.Text;

namespace ML.Core.Configuration;

/// <summary>
/// 配置里的一条「Minecraft 目录」。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Note"/> 是给人认的备注，默认取文件夹名，改它**不动磁盘上的文件夹**。
/// </para>
/// <para>
/// <see cref="Id"/> 由路径算出而不是随机生成：同一个路径永远是同一个 id，
/// 所以「重复添加同一目录」能直接认出来，实例里记的 <c>rootId</c> 也不会因为重启而对不上。
/// </para>
/// </remarks>
public sealed class GameRootEntry
{
    /// <summary>目录的绝对路径。</summary>
    public string Path { get; init; } = "";

    /// <summary>备注名，默认是文件夹名。</summary>
    public string Note { get; init; } = "";

    /// <summary>稳定 id，见类型注释。</summary>
    public string Id { get; init; } = "";

    /// <summary>把用户挑的路径整理成一条记录；空备注取文件夹名。</summary>
    public static GameRootEntry FromPath(string path, string? note = null)
    {
        string full = NormalizePath(path);
        string trimmed = note?.Trim() ?? "";
        string label = trimmed.Length > 0 ? trimmed : FolderNameOf(full);

        return new GameRootEntry { Path = full, Note = label, Id = MakeId(full) };
    }

    /// <summary>路径里的最后一段，取不到就原样返回，避免备注变成空串。</summary>
    public static string FolderNameOf(string path)
    {
        string trimmed = System.IO.Path.TrimEndingDirectorySeparator(path);
        string name = System.IO.Path.GetFileName(trimmed);

        return string.IsNullOrWhiteSpace(name) ? trimmed : name;
    }

    public static string NormalizePath(string path) =>
        System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path.Trim()));

    /// <summary>路径哈希。大小写不敏感——Windows 上 <c>C:\MC</c> 和 <c>c:\mc</c> 是同一个地方。</summary>
    public static string MakeId(string path)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(NormalizePath(path).ToLowerInvariant()));

        return "r-" + Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    /// <summary>整理一条已有的记录：路径必须能规范化，id/备注缺了就补。</summary>
    public GameRootEntry Normalize()
    {
        string full = NormalizePath(Path);
        string note = string.IsNullOrWhiteSpace(Note) ? FolderNameOf(full) : Note.Trim();

        return new GameRootEntry
        {
            Path = full,
            Note = note,
            Id = string.IsNullOrWhiteSpace(Id) ? MakeId(full) : Id
        };
    }
}
