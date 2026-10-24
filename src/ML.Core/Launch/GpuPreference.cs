using System.Diagnostics;
using Microsoft.Win32;

namespace ML.Core.Launch;

/// <summary>
/// 「使用高性能显卡」：把启动器和 javaw 写进 Windows 的按应用显卡偏好。
/// </summary>
/// <remarks>
/// <para>
/// 这是 Windows 10 1803+ 的 <c>HKCU\Software\Microsoft\DirectX\UserGpuPreferences</c>，
/// 值名是可执行文件全路径，值是 <c>GpuPreference=2;</c>（2 = 高性能）。
/// </para>
/// <para>
/// <b>它只对"双显卡笔记本默认用核显"这种情况有用。</b>台式机只有一块显卡时写了也没意义，
/// 但也不会出错。写不进去（权限/系统太老）就跳过，不影响启动。
/// </para>
/// </remarks>
public static class GpuPreference
{
    private const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string HighPerformance = "GpuPreference=2;";

    /// <summary>把给定可执行文件设成"高性能"。返回实际写入成功个数。</summary>
    public static int PreferHighPerformance(IEnumerable<string> executablePaths)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        int written = 0;

        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(KeyPath);

            if (key is null)
            {
                return 0;
            }

            foreach (string path in executablePaths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct())
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    key.SetValue(path, HighPerformance, RegistryValueKind.String);
                    written++;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    // 单个写不进去就跳过，别因为显卡偏好把启动流程搞崩。
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Debug.WriteLine($"[gpu] 写注册表失败：{ex.Message}");
        }

        return written;
    }
}
