using System.Text.Json;
using KClevoPanel.Core;

if (args.Contains("--hardware-only"))
{
    Console.WriteLine(JsonSerializer.Serialize(await HardwareInventory.ReadAsync(), new JsonSerializerOptions { WriteIndented = true }));
    return;
}

if (args.Contains("--self-test"))
{
    void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine($"PASS {name}"); }
    await DirectPresetTests.Run(Check);
    FanCurveTests.Run(Check);
    Check(CpuMath.Percent(100, 200, 100, 150, 300, 200) == 75, "CPU kernel includes idle");
    Check(CpuMath.Percent(100, 200, 100, 100, 200, 100) is null, "No elapsed time is unavailable");
    Check(CpuMath.Percent(100, 200, 100, 90, 300, 200) is null, "Counter reset is unavailable");
    Check(CpuMath.Percent(100, 200, 100, 400, 300, 200) is null, "Invalid idle delta is unavailable");
    var root = StaticInventory.ReadDll(@"C:\Program Files (x86)\ControlCenter\InsydeDCHU.dll");
    var dchu = StaticInventory.ReadDll(@"C:\Program Files (x86)\ControlCenter\DCHU\InsydeDCHU.dll");
    Check(root.Error is null && root.Exports.Length == 5 && !root.Exports.Contains("SetDCHU_DataEx"), "Root DLL export capabilities");
    Check(dchu.Error is null && dchu.Exports.Length == 6 && dchu.Exports.Contains("SetDCHU_DataEx"), "DCHU DLL export capabilities");
    Check(dchu.Sha256 == "D385DEF14C5AA6CBB8EB60FD34D0EFEB92EAF39E875AB28DE05FE0FDAA7CAB9C", "Target DLL identity");
    Check(StaticInventory.ReadDll("missing-test-file.dll").Error is not null, "Missing DLL remains unavailable");
    var activeLibrary = TargetDchuLibrary.Resolve();
    Check(activeLibrary.Sha256 == TargetDchuLibrary.DriverStoreHash && activeLibrary.Exports.Contains("SetDCHU_Data"), "Verified DriverStore library resolves without a ControlCenter process");
    var identity = StaticInventory.ReadFirmwareIdentity();
    Check(identity.Errors.Length == 0 && identity.Model == "COLORFUL X17 AT 23H2" && identity.Bios == "1.07.04TCOLO"
        && identity.Drivers.Any(d => d.Version == "1.0.0.7") && identity.Drivers.Any(d => d.Version == "1.0.40.0"), "Filtered firmware identity preserves all model BIOS and bridge guards");
    using (var fast = new WindowsTelemetry())
    {
        var first = fast.Read(includeGpu: false);
        Check(first.MemoryTotalGiB > 0 && first.Gpu.Name is null && first.Gpu.State == "初始化中", "Fast system read does not initialize or query NVML");
        fast.Dispose();
        bool disposedRejected = false;
        try { fast.Read(includeGpu: false); } catch (ObjectDisposedException) { disposedRejected = true; }
        Check(disposedRejected, "Late read after disposal cannot reopen native resources");
    }
    var history = new SeriesHistory();
    var now = DateTimeOffset.Now;
    history.Add(now, 40); history.Add(now.AddSeconds(30), null); history.Add(now.AddSeconds(61), 50);
    Check(history.Points.Count == 3 && history.Points[0].Value == 40 && history.Points[1].Value is null, "History keeps a boundary predecessor and preserves missing samples");
    history.Add(now.AddSeconds(61), 55);
    Check(history.Points.Count == 3 && history.Points[^1].Value == 55, "Duplicate timestamp replaces last value");
    history.Add(now, double.NaN);
    Check(history.Points.Count == 1 && history.Points[0].Value is null, "Clock reversal resets history and invalid values are gaps");
    var rolling = new SeriesHistory();
    for (int i = 0; i <= 130; i++) rolling.Add(now.AddMilliseconds(i * 500), i);
    Check(rolling.Points[0].Time < now.AddSeconds(5) && rolling.Points[1].Time >= now.AddSeconds(5), "Rolling window retains exactly one predecessor for clipping");
    rolling.Add(now.AddHours(1), 42);
    Check(rolling.Points.Count == 2 && rolling.Points[^1].Value == 42, "Long gaps keep bounded history without filling missing time");
    var b12 = new byte[256]; var b13 = new byte[256]; b13[12] = 2;
    b12[18] = 80; b12[21] = 60; b12[2] = 1; b12[3] = 46; b12[4] = 1; b12[5] = 48;
    b12[16] = 255; b12[19] = 128;
    var thermal = ThermalDecoder.Decode(b12,b13,12,13,1);
    Check(thermal.CpuTemperatureC == 80 && thermal.Fans[0].Rpm == 7140, "Thermal byte layout and vendor fan formula");
    Check(ThermalDecoder.FanRpm(65535) is null && ThermalDecoder.FanRpm(0) == 0, "Fan unavailable sentinel differs from zero RPM");
    bool invalidRejected = false;
    try { ThermalDecoder.Decode(new byte[256],b13,12,13,1); } catch (InvalidDataException) { invalidRejected = true; }
    Check(invalidRejected, "Empty firmware response cannot become telemetry");
    Check(HardwareInventory.BinarySize(17179869184) == "16 GiB", "Hardware memory capacity uses binary GiB");
    Check(HardwareInventory.DiskSize(1000000000000) == "1 TB", "Hardware disk capacity uses decimal TB");
    Check(HardwareInventory.BinarySize(null) == "未报告" && HardwareInventory.DiskSize(0) == "未报告", "Missing hardware capacity is not a fabricated zero");
    return;
}
using var telemetry = new WindowsTelemetry();
telemetry.Read();
await Task.Delay(1200);
var snapshot = telemetry.Read();
var machine = StaticInventory.Read();
Console.WriteLine(JsonSerializer.Serialize(new { Machine = machine, Snapshot = snapshot,
    DchuState = "StaticOnly: no vendor DLL loaded, no firmware requests" }, new JsonSerializerOptions { WriteIndented = true }));
