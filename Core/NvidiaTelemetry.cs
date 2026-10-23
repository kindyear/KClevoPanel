using System.Runtime.InteropServices;
using System.Text;

namespace KClevoPanel.Core;

// This backend binds only documented NVIDIA query APIs. No control APIs exist here.
internal sealed class NvidiaTelemetry : IDisposable
{
    private IntPtr _library, _device;
    private bool _tried, _initialized;
    private string? _name;
    private string _state = "尚未采样";
    private ValueQuery? _temperature, _clock;
    private SimpleQuery? _power;
    private UtilQuery? _utilization;
    private Init? _shutdown;
    [StructLayout(LayoutKind.Sequential)] private struct Utilization { public uint Gpu, Memory; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Init();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Count(out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Handle(uint index, out IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NameQuery(IntPtr handle, byte[] name, uint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ValueQuery(IntPtr handle, uint kind, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SimpleQuery(IntPtr handle, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int UtilQuery(IntPtr handle, out Utilization value);

    private T Function<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
    private void Initialize()
    {
        if (_tried) return;
        _tried = true;
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvml.dll");
        if (!File.Exists(path)) { _state = "NVML 不可用"; return; }
        try
        {
            _library = NativeLibrary.Load(path);
            _shutdown = Function<Init>("nvmlShutdown");
            int result = Function<Init>("nvmlInit_v2")();
            if (result != 0) { _state = $"NVML 初始化不可用（{result}）"; return; }
            _initialized = true;
            if (Function<Count>("nvmlDeviceGetCount_v2")(out uint count) != 0 || count == 0)
            { _state = "未发现 NVIDIA GPU"; return; }
            // NVML enumerates NVIDIA devices only, unlike the Windows display adapter list.
            if (Function<Handle>("nvmlDeviceGetHandleByIndex_v2")(0, out _device) != 0)
            { _state = "NVIDIA GPU 暂不可用"; return; }
            var bytes = new byte[128];
            if (Function<NameQuery>("nvmlDeviceGetName")(_device, bytes, (uint)bytes.Length) == 0)
                _name = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
            _temperature = Function<ValueQuery>("nvmlDeviceGetTemperature");
            _clock = Function<ValueQuery>("nvmlDeviceGetClockInfo");
            _power = Function<SimpleQuery>("nvmlDeviceGetPowerUsage");
            _utilization = Function<UtilQuery>("nvmlDeviceGetUtilizationRates");
        }
        catch (Exception e) { _device = IntPtr.Zero; _state = $"NVML 不可用：{e.GetType().Name}"; }
    }
    public GpuReading Read()
    {
        Initialize();
        if (_device == IntPtr.Zero || _temperature is null || _clock is null || _power is null || _utilization is null)
            return new(_name, null, null, null, null, _state, "NVML");
        double? temp = null, watts = null, clock = null, util = null;
        if (_temperature(_device, 0, out uint t) == 0) temp = t;
        if (_power(_device, out uint p) == 0) watts = p / 1000.0;
        if (_clock(_device, 0, out uint c) == 0) clock = c;
        if (_utilization(_device, out var u) == 0 && u.Gpu <= 100) util = u.Gpu;
        bool any = temp.HasValue || watts.HasValue || clock.HasValue || util.HasValue;
        return new(_name, temp, watts, clock, util, any ? "实时读取" : "独显休眠或指标暂不可用", "NVML");
    }
    public void Dispose()
    {
        if (_initialized) _shutdown?.Invoke();
        _initialized = false; _device = IntPtr.Zero;
        if (_library != IntPtr.Zero) NativeLibrary.Free(_library);
        _library = IntPtr.Zero;
    }
}
