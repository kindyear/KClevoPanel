namespace KClevoPanel.Core;

public sealed record DllInfo(string Path, long? Size, string? Sha256, string[] Exports, string? Error);
public sealed record DriverInfo(string Name, string Version, string Inf);
public sealed record FirmwareIdentity(string Model, string Bios, DriverInfo[] Drivers, string[] Errors);
public sealed record MachineInfo(string Manufacturer, string Model, string Bios, string Os,
    string Cpu, string[] Gpus, DriverInfo[] Drivers, DllInfo[] Dlls, DateTimeOffset CapturedAt, string[] Errors);
public sealed record GpuReading(string? Name, double? TemperatureC, double? Watts, double? ClockMhz,
    double? UtilizationPercent, string State, string Source);
public sealed record Snapshot(DateTimeOffset CapturedAt, double? CpuPercent, double? MemoryUsedGiB,
    double? MemoryTotalGiB, double? BatteryPercent, bool? OnAc, bool? Charging,
    GpuReading Gpu, string[] Errors)
{
    public ThermalReading? Thermal { get; init; }
    public DateTimeOffset? GpuCapturedAt { get; init; }
}

public sealed record FanReading(string Name, int? Rpm, double? DutyPercent, int Raw);
public sealed record ThermalReading(DateTimeOffset CapturedAt, double? CpuTemperatureC, double? GpuTemperatureC,
    FanReading[] Fans, string State, int Rc12, int Rc13, byte[] Raw12, byte[] Raw13, bool BoundsIntact, double QueryMilliseconds);

public static class CpuMath
{
    // GetSystemTimes kernel time includes idle time.
    public static double? Percent(ulong oldIdle, ulong oldKernel, ulong oldUser,
        ulong idle, ulong kernel, ulong user)
    {
        if (idle < oldIdle || kernel < oldKernel || user < oldUser) return null;
        double total = (double)(kernel - oldKernel) + (user - oldUser);
        double inactive = idle - oldIdle;
        if (total <= 0 || inactive > total) return null;
        return Math.Clamp((total - inactive) / total * 100, 0, 100);
    }
}
