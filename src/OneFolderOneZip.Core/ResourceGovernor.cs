using System.Runtime.InteropServices;

namespace OneFolderOneZip.Core;

public sealed class ResourceGovernor : IDisposable
{
    private ulong _idle, _kernel, _user;
    private IntPtr _query, _disk;
    private readonly int _maximum = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
    public int Limit { get; private set; } = 1;

    public ResourceGovernor()
    {
        _ = GetSystemTimes(out _idle, out _kernel, out _user);
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) == 0
            && PdhAddEnglishCounter(_query, @"\PhysicalDisk(_Total)\% Disk Time", IntPtr.Zero, out _disk) == 0)
            _ = PdhCollectQueryData(_query);
    }

    public void Sample()
    {
        double cpu = 0, disk = 0;
        if (GetSystemTimes(out ulong idle, out ulong kernel, out ulong user))
        {
            ulong total = kernel - _kernel + user - _user;
            if (total > 0) cpu = 100d * (total - Math.Min(total, idle - _idle)) / total;
            (_idle, _kernel, _user) = (idle, kernel, user);
        }
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        _ = GlobalMemoryStatusEx(ref memory);
        if (_disk != IntPtr.Zero && PdhCollectQueryData(_query) == 0
            && PdhGetFormattedCounterValue(_disk, 0x200, out _, out CounterValue value) == 0 && value.Status == 0)
            disk = value.Value;
        int desired = cpu > 85 || memory.Load > 85 || disk > 90 ? 1
            : cpu > 65 || memory.Load > 75 || disk > 65 ? Math.Max(1, _maximum / 2) : _maximum;
        Limit = desired < Limit ? desired : Math.Min(desired, Limit + 1);
    }

    public void Dispose() { if (_query != IntPtr.Zero) { _ = PdhCloseQuery(_query); _query = IntPtr.Zero; } }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    { public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, AvailableExtended; }
    [StructLayout(LayoutKind.Explicit)] private struct CounterValue
    { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double Value; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")] private static extern uint PdhOpenQuery(string? source, IntPtr data, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")] private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr data, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out CounterValue value);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}
