using System.Runtime.InteropServices;
using System.Text;

namespace ML.Core.Launch;

/// <summary>
/// 把含非 ASCII 的路径换成 Windows 的 8.3 短路径。
/// </summary>
/// <remarks>
/// <para>
/// 用途只有一个：Java 18 以前的版本读命令行时按系统 ANSI 代码页解码，
/// 中文路径会被解成乱码，于是原本能跑的游戏在中文目录下直接起不来。
/// PCL 用一个外部 wrapper 程序解决这件事；我们不额外带 exe，改成把路径换成纯 ASCII 的短路径
/// （<c>C:\程序\java.exe</c> → <c>C:\A1B2~1\java.exe</c>）。目的相同，少一个要维护的二进制。
/// </para>
/// <para>
/// 短路径要求该卷没关 8.3 名称生成（NTFS 默认开）；关了的卷上拿不到就原样返回。
/// </para>
/// </remarks>
public static class ShortPath
{
    /// <summary>能转就转，转不了/本来就是 ASCII 就原样返回。</summary>
    public static string TryGet(string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path) || IsAscii(path))
        {
            return path;
        }

        var buffer = new StringBuilder(1024);
        uint length = GetShortPathName(path, buffer, (uint)buffer.Capacity);

        return length > 0 && length < buffer.Capacity ? buffer.ToString() : path;
    }

    public static bool IsAscii(string path) => path.All(char.IsAscii);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint size);
}
