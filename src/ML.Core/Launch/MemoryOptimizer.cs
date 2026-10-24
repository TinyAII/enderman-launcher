using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ML.Core.Launch;

/// <summary>
/// 「启动游戏前进行内存优化」。
/// </summary>
/// <remarks>
/// <para>
/// <b>它做的事必须说清楚：只是把各进程的工作集（working set）换出到页面文件，让"已用内存"数字立刻变好看。</b>
/// 物理内存并没有凭空变多——被换出去的页迟早还要读回来。所以它对你正在玩的游戏没有加速作用，
/// 真正有用的场景是"机器被别的程序占满、游戏申请不到连续内存"。
/// </para>
/// <para>
/// 机械硬盘上这一步会让系统立刻去刷盘，可能卡住几秒——PCL 的提示里也写了这一点，我们照抄这个诚实。
/// </para>
/// <para>
/// 权限不够的进程直接跳过，不报错：这是尽力而为的清理，不是必须成功的操作。
/// </para>
/// </remarks>
public static class MemoryOptimizer
{
    /// <summary>清理一遍，返回（尝试数、成功数）。</summary>
    public static (int Tried, int Trimmed) TrimWorkingSets()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (0, 0);
        }

        int tried = 0;
        int trimmed = 0;

        // 自己的进程也清一下：启动器本身占的几 MB 也算。
        if (EmptyWorkingSet(Process.GetCurrentProcess().Handle))
        {
            trimmed++;
        }

        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                // 只动"当前用户能打开的"进程；系统进程大多打不开，跳过是正常的。
                if (process.Id <= 4)
                {
                    continue;
                }

                tried++;

                try
                {
                    if (EmptyWorkingSet(process.Handle))
                    {
                        trimmed++;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // 没权限、进程已退出：都不算失败，继续下一个。
                }
            }
        }

        return (tried, trimmed);
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr process);
}
