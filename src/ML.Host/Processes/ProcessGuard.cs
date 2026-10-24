using System.Runtime.InteropServices;

namespace ML.Host.Processes;

/// <summary>
/// 子进程托管：把插件起的进程绑到宿主的 Job Object 上。
/// </summary>
/// <remarks>
/// <para>
/// 只做一件事，但这件事插件自己做不可靠：<b>启动器死掉之后，把它起的进程一起带走</b>。
/// </para>
/// <para>
/// 用的是 <c>KILL_ON_JOB_CLOSE</c>：宿主进程无论怎么没的（正常退出、崩溃、任务管理器结束），
/// 操作系统都会因为我们那个 Job 句柄关闭而清掉 Job 里的全部进程——包括它们自己再拉起来的子孙。
/// </para>
/// <para>
/// 起进程、读输出、发命令、停止仍然是插件自己的事，用的就是标准 BCL 的 <c>Process</c>。
/// 这里不掺和，只是给它们上一道"死也一起死"的保险。
/// </para>
/// </remarks>
internal sealed class ProcessGuard : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private readonly IntPtr _job;
    private readonly Action<string> _log;
    private bool _disposed;

    public ProcessGuard(Action<string> log)
    {
        _log = log;
        _job = CreateJob();

        if (_job == IntPtr.Zero)
        {
            _log("创建 Job Object 失败：插件起的进程不会随启动器退出而结束");
        }
    }

    /// <summary>把 pid 绑进 Job。失败只说明这一次没托管上，进程本身照跑。</summary>
    public bool Adopt(int processId)
    {
        if (_disposed || _job == IntPtr.Zero || processId <= 0)
        {
            return false;
        }

        IntPtr handle = OpenProcess(
            ProcessAccessRights.SetQuota | ProcessAccessRights.Terminate | ProcessAccessRights.QueryLimited,
            false,
            processId);

        if (handle == IntPtr.Zero)
        {
            _log($"托管进程 {processId} 失败：打不开句柄（可能已经退出）");
            return false;
        }

        try
        {
            if (!AssignProcessToJobObject(_job, handle))
            {
                _log($"托管进程 {processId} 失败：AssignProcessToJobObject 返回 false（错误码 {Marshal.GetLastWin32Error()}）");
                return false;
            }

            return true;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 关掉句柄 → Job 里所有进程被系统清掉。
        // 正常退出走这里；崩溃时是操作系统替我们做同一件事。
        if (_job != IntPtr.Zero)
        {
            CloseHandle(_job);
        }
    }

    private IntPtr CreateJob()
    {
        try
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);

            if (job == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

            int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr buffer = Marshal.AllocHGlobal(length);

            try
            {
                Marshal.StructureToPtr(info, buffer, false);

                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)length))
                {
                    CloseHandle(job);
                    return IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return job;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // 非 Windows 上没有这套 API
            return IntPtr.Zero;
        }
    }

    [Flags]
    private enum ProcessAccessRights : uint
    {
        Terminate = 0x0001,
        SetQuota = 0x0100,
        QueryLimited = 0x1000
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(ProcessAccessRights access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
