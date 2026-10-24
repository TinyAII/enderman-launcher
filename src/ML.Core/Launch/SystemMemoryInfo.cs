using System.Runtime.InteropServices;

namespace ML.Core.Launch;

/// <summary>物理内存现状。设置页那条"已使用 / 总量"就是从这里来的。</summary>
public sealed record SystemMemoryInfo(long TotalMb, long AvailableMb)
{
    public long UsedMb => TotalMb - AvailableMb;

    /// <summary>取一次当前值。拿不到时返回全 0，调用方据此不显示任何数字而不是显示假数据。</summary>
    public static SystemMemoryInfo Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new SystemMemoryInfo(0, 0);
        }

        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };

        if (!GlobalMemoryStatusEx(ref status))
        {
            return new SystemMemoryInfo(0, 0);
        }

        return new SystemMemoryInfo(
            (long)(status.TotalPhys / (1024 * 1024)),
            (long)(status.AvailPhys / (1024 * 1024)));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
