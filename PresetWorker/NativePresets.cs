using System.Runtime.InteropServices;
using KClevoPanel.Core;

namespace KClevoPanel.PresetWorker;

internal sealed class NativePresets : IDirectPresetTransport, IFanCurveTransport, IDisposable
{
    private readonly IntPtr _library;
    private readonly Settings _read;
    private bool _faulted;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Settings(int page, int offset, int length, IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Setter(int command, IntPtr buffer, int length);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Getter(int command, IntPtr output);
    public long FirmwareWriteCalls { get; private set; }
    public long MirrorWriteCalls { get; private set; }
    public DllInfo Library { get; }
    public int? LastCurveReturn { get; private set; }
    public int? LastFanModeReturn { get; private set; }
    public int? LastFanMirrorReturn { get; private set; }
    public NativePresets()
    {
        var machine = StaticInventory.ReadFirmwareIdentity();
        if (machine.Errors.Length != 0 || machine.Model != "COLORFUL X17 AT 23H2" || machine.Bios != "1.07.04TCOLO"
            || !machine.Drivers.Any(d => d.Version == "1.0.0.7") || !machine.Drivers.Any(d => d.Version == "1.0.40.0"))
            throw new InvalidOperationException("当前设备、BIOS或桥驱动未在直接接口配置中验证：" + System.Text.Json.JsonSerializer.Serialize(machine));
        Library = TargetDchuLibrary.Resolve();
        if (!new[] { "ReadAppSettings", "WriteAppSettings", "SetDCHU_Data" }.All(Library.Exports.Contains))
            throw new InvalidOperationException("本机DCHU库与已核对版本不一致");
        _library = LoadLibraryExW(Library.Path, IntPtr.Zero, 0x100 | 0x800);
        if (_library == IntPtr.Zero) throw new InvalidOperationException("无法加载本机DCHU库");
        try { _read = Bind<Settings>("ReadAppSettings"); }
        catch { FreeLibrary(_library); throw; }
    }
    private T Bind<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(GetProcAddress(_library, name));
    public PresetSnapshot Read()
    {
        // Only two factory settings pages, exact offsets used by the original mode implementation.
        byte[] p1 = ReadSettingsPage(1), p4 = ReadSettingsPage(4);
        return new(DateTimeOffset.Now, p1[1], p1[32], p4[8], p4[5], p4[7]);
    }
    private byte[] ReadSettingsPage(int page)
    {
        EnsureHealthy();
        if (page is not (1 or 4)) throw new InvalidOperationException("非白名单页");
        using var buffer = new GuardedBuffer();
        int rc = _read(page, 0, 256, buffer.Pointer);
        byte[] bytes;
        try { bytes = buffer.Read(256); } catch { _faulted = true; throw; }
        if (rc != 0x1000) throw new IOException("设置页读取未成功");
        return bytes;
    }
    public ThermalReading ReadThermals()
    {
        EnsureHealthy();
        var get = Bind<Getter>("GetDCHU_Data_Buffer");
        byte[] ReadBlock(int command)
        {
            using var output = new GuardedBuffer();
            int rc = get(command, output.Pointer);
            byte[] block;
            try
            {
                block = output.Read(257);
                if (block[256] != 0xCD && block[256] != block[0]) throw new InvalidDataException("遥测追加字节异常");
            }
            catch { _faulted = true; throw; }
            if (rc != command) throw new IOException("遥测命令未成功");
            return block[..256];
        }
        var cap = ReadBlock(13); var telemetry = ReadBlock(12);
        return ThermalDecoder.Decode(telemetry, cap, 12, 13, 0);
    }
    public int SetFirmwareMode(byte[] payload)
    {
        EnsureHealthy();
        if (payload.Length != 4 || payload[1] != 0 || payload[2] != 0 || payload[3] != 25
            || payload[0] is not (0 or 1 or 2 or 3 or 0x42)) throw new InvalidOperationException("载荷不在模式白名单中");
        using var input = new GuardedBuffer(payload);
        // These exports are resolved only on an explicitly requested transaction, never on reads/plans.
        var set = Bind<Setter>("SetDCHU_Data");
        FirmwareWriteCalls++;
        int rc = set(121, input.Pointer, 4);
        CheckInput(input);
        return rc;
    }
    public int SaveSelection(int mode)
    {
        EnsureHealthy();
        if (mode is < 0 or > 3) throw new InvalidOperationException("非白名单档位");
        using var input = new GuardedBuffer([(byte)mode]);
        var write = Bind<Settings>("WriteAppSettings");
        MirrorWriteCalls++;
        int rc = write(1, 1, 1, input.Pointer);
        CheckInput(input);
        return rc;
    }
    public FanCurveSnapshot ReadFanCurves() => FanCurveSnapshot.Decode(ReadThermals(), Read());
    public int SetFanCurve(FanCurve[] curves)
    {
        EnsureHealthy();
        using var input = new GuardedBuffer(FanCurveProtocol.Frame(curves));
        var set = Bind<Setter>("SetDCHU_Data");
        FirmwareWriteCalls++;
        int rc = set(14, input.Pointer, 256);
        LastCurveReturn = rc;
        CheckInput(input);
        return rc;
    }
    public int SetFanMode(int mode)
    {
        EnsureHealthy();
        if (mode is not (0 or 1 or 6)) throw new InvalidOperationException("非白名单风扇模式");
        using var input = new GuardedBuffer([(byte)mode, 0, 0, 1]);
        var set = Bind<Setter>("SetDCHU_Data");
        FirmwareWriteCalls++;
        int rc = set(121, input.Pointer, 4); LastFanModeReturn = rc; CheckInput(input); return rc;
    }
    public int SaveFanMode(int mode)
    {
        EnsureHealthy();
        if (mode is not (0 or 1 or 6)) throw new InvalidOperationException("非白名单风扇模式");
        using var input = new GuardedBuffer([(byte)mode]);
        var write = Bind<Settings>("WriteAppSettings");
        MirrorWriteCalls++;
        int rc = write(4, 5, 1, input.Pointer); LastFanMirrorReturn = rc; CheckInput(input); return rc;
    }
    private void CheckInput(GuardedBuffer buffer)
    { try { buffer.VerifyInputUnchanged(); } catch { _faulted = true; throw; } }
    private void EnsureHealthy() { if (_faulted) throw new InvalidOperationException("原生缓冲区异常，已停止写入"); }
    public void Dispose() => FreeLibrary(_library);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern IntPtr LoadLibraryExW(string file, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
}

internal sealed class GuardedBuffer : IDisposable
{
    private readonly IntPtr _allocation;
    private readonly byte[] _initial;
    public IntPtr Pointer { get; }
    public GuardedBuffer(byte[]? input = null)
    {
        int page = Environment.SystemPageSize;
        _allocation = VirtualAlloc(IntPtr.Zero, (UIntPtr)(page * 3), 0x3000, 1);
        if (_allocation == IntPtr.Zero) throw new OutOfMemoryException();
        Pointer = _allocation + page;
        if (!VirtualProtect(Pointer, (UIntPtr)page, 4, out _)) { Dispose(); throw new IOException("保护页设置失败"); }
        _initial = Enumerable.Repeat((byte)0xCD, page).ToArray();
        if (input is not null) Array.Copy(input, _initial, input.Length);
        Marshal.Copy(_initial, 0, Pointer, _initial.Length);
    }
    public byte[] Read(int length)
    {
        var bytes = new byte[_initial.Length]; Marshal.Copy(Pointer, bytes, 0, bytes.Length);
        if (bytes.Skip(length).Where((b, i) => b != _initial[length + i]).Any()) throw new InvalidDataException("输出超过已验证边界");
        return bytes[..length];
    }
    public void VerifyInputUnchanged()
    { var bytes = new byte[_initial.Length]; Marshal.Copy(Pointer, bytes, 0, bytes.Length); if (!bytes.SequenceEqual(_initial)) throw new InvalidDataException("原生调用改写了只读输入"); }
    public void Dispose() => VirtualFree(_allocation, UIntPtr.Zero, 0x8000);
    [DllImport("kernel32.dll")] private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll")] private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protect, out uint oldProtect);
    [DllImport("kernel32.dll")] private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint type);
}
