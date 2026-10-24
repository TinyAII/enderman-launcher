using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace ML.Core.Launch;

/// <summary>
/// 目录联接（junction）：把「中文路径」映射成一个纯 ASCII 路径。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不是 8.3 短路径：实测 <c>GetShortPathName</c> 给中文目录生成的短名**仍然带中文**
/// （<c>中文探针</c> → <c>中文探~1</c>），救不了 Java 18 以前按 ANSI 解码命令行的毛病。
/// </para>
/// <para>
/// junction 和 symlink 的区别很关键：<b>junction 不需要管理员权限</b>，symlink 需要。
/// 所以这里用 <c>mklink /J</c>。
/// </para>
/// </remarks>
public static class Junction
{
    /// <summary>确保 <paramref name="link"/> 指向 <paramref name="target"/>。已存在就直接复用。</summary>
    public static bool Ensure(string link, string target, out string? error)
    {
        error = null;

        if (!OperatingSystem.IsWindows())
        {
            error = "只支持 Windows";
            return false;
        }

        if (Directory.Exists(link))
        {
            // 已经建过（同一台机器上路径不变就一直是同一个链接）
            return true;
        }

        try
        {
            string? parent = Path.GetDirectoryName(link);

            if (parent is not null)
            {
                Directory.CreateDirectory(parent);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"建不了链接目录：{ex.Message}";
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = Process.Start(startInfo);

            if (process is null)
            {
                error = "无法启动 mklink";
                return false;
            }

            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0 || !Directory.Exists(link))
            {
                error = output.Trim() is { Length: > 0 } text ? text : $"mklink 退出码 {process.ExitCode}";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>给一串文本生成 8 位十六进制标签，用作链接名（纯 ASCII，稳定可复用）。</summary>
    public static string Tag(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));

        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }
}
