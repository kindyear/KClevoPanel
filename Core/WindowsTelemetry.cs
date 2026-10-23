using System.Runtime.InteropServices;

namespace KClevoPanel.Core;

public sealed class WindowsTelemetry : IDisposable
{
    private readonly NvidiaTelemetry _nvidia = new();
    private (ulong Idle, ulong Kernel, ulong User)? _previous;
    private readonly object _sync = new();
    private readonly object _gpuSync = new();
    private volatile bool _disposed;

    public Snapshot Read(bool includeGpu = true)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var errors = new List<string>();
            double? cpu = null, used = null, total = null, battery = null;
            bool? ac = null, charging = null;
            if (GetSystemTimes(out var idle, out var kernel, out var user))
            {
                if (_previous is { } p) cpu = CpuMath.Percent(p.Idle, p.Kernel, p.User, idle, kernel, user);
                _previous = (idle, kernel, user);
            }
            else errors.Add("CPU 采样暂不可用");
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref memory))
            {
                total = memory.TotalPhysical / 1073741824.0;
                used = (memory.TotalPhysical - memory.AvailablePhysical) / 1073741824.0;
            }
            else errors.Add("内存采样暂不可用");
            if (GetSystemPowerStatus(out var power))
            {
                if (power.AcLineStatus <= 1) ac = power.AcLineStatus == 1;
                if (power.BatteryFlag != 255 && (power.BatteryFlag & 128) == 0)
                {
                    if (power.BatteryLifePercent <= 100) battery = power.BatteryLifePercent;
                    charging = (power.BatteryFlag & 8) != 0;
                }
            }
            else errors.Add("电池采样暂不可用");
            return new(DateTimeOffset.Now, cpu, used, total, battery, ac, charging,
                includeGpu ? ReadGpu() : new(null, null, null, null, null, "初始化中", "NVML"), errors.ToArray());
        }
    }
    public GpuReading ReadGpu() { lock (_gpuSync) { ObjectDisposedException.ThrowIf(_disposed, this); return _nvidia.Read(); } }
    public void Dispose() { _disposed = true; lock (_sync) lock (_gpuSync) _nvidia.Dispose(); }
    public void ResetCpuBaseline() { lock (_sync) _previous = null; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, Reserved;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out PowerStatus power);
}
