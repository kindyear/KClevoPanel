using System.Runtime.InteropServices;
using KClevoPanel.Core;

namespace KClevoPanel.TelemetryWorker;
internal sealed class GuardedDchuReader : IDisposable
{
    private readonly IntPtr _library;
    private readonly Getter _get;
    public (int Rc, byte[] Bytes) Capability { get; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Getter(int command, IntPtr output);
    public GuardedDchuReader()
    {
        var machine = StaticInventory.ReadFirmwareIdentity();
        if (machine.Errors.Length != 0 || machine.Model != "COLORFUL X17 AT 23H2" || machine.Bios != "1.07.04TCOLO"
            || !machine.Drivers.Any(d => d.Version == "1.0.0.7") || !machine.Drivers.Any(d => d.Version == "1.0.40.0"))
            throw new InvalidOperationException("当前机型、BIOS或桥驱动未在只读配置中验证：" + System.Text.Json.JsonSerializer.Serialize(machine));
        var dll = TargetDchuLibrary.Resolve();
        if (!dll.Exports.Contains("GetDCHU_Data_Buffer"))
            throw new InvalidOperationException("本机DCHU库与已核对版本不一致");
        _library = LoadLibraryExW(dll.Path, IntPtr.Zero, 0x100 | 0x800);
        if (_library == IntPtr.Zero) throw new InvalidOperationException("无法加载本机只读通道库");
        try
        {
            _get = Marshal.GetDelegateForFunctionPointer<Getter>(GetProcAddress(_library, "GetDCHU_Data_Buffer"));
            Capability = ReadFixedBlock(13);
        }
        catch { FreeLibrary(_library); throw; }
    }
    public (int Rc, byte[] Bytes) ReadTelemetry() => ReadFixedBlock(12);
    public (int Rc, byte[] Bytes) ReadCapability() => ReadFixedBlock(13);
    private (int Rc, byte[] Bytes) ReadFixedBlock(int command)
    {
        if (command is not (12 or 13)) throw new InvalidOperationException("非白名单查询");
        int page = Environment.SystemPageSize, writable = page * 2;
        var allocation = VirtualAlloc(IntPtr.Zero, (UIntPtr)(page * 4), 0x3000, 1); // no-access pages before/after
        if (allocation == IntPtr.Zero) throw new OutOfMemoryException();
        try
        {
            var buffer = allocation + page;
            if (!VirtualProtect(buffer, (UIntPtr)writable, 4, out _)) throw new InvalidOperationException("无法设置采样保护页");
            var initial = Enumerable.Repeat((byte)0xCD, writable).ToArray();
            Array.Clear(initial, 0, 256);
            Marshal.Copy(initial, 0, buffer, initial.Length);
            int result = _get(command, buffer);
            var returned = new byte[writable]; Marshal.Copy(buffer, returned, 0, returned.Length);
            // This exact DLL can append byte 257. It is accommodated, not written past an array.
            if (returned[256] != 0xCD && returned[256] != returned[0]) throw new InvalidDataException("返回缓冲区布局异常");
            if (returned.Skip(257).Any(b => b != 0xCD)) throw new InvalidDataException("返回缓冲区超过已验证边界");
            return (result, returned[..256]);
        }
        finally { VirtualFree(allocation, UIntPtr.Zero, 0x8000); }
    }
    public void Dispose() => FreeLibrary(_library);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern IntPtr LoadLibraryExW(string file, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protect, out uint oldProtect);
    [DllImport("kernel32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint type);
}
