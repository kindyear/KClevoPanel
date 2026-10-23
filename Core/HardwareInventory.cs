using System.Globalization;

namespace KClevoPanel.Core;

public sealed record HardwareProperty(string Label, string Value);
public sealed record HardwareDevice(string Name, string Summary, HardwareProperty[] Properties,
    bool IsVirtual = false, bool IsExternal = false, ulong? CapacityBytes = null);
public sealed record HardwareCategory(string Key, string Title, string Glyph, HardwareDevice[] Devices, string[] Errors);
public sealed record HardwareReport(DateTimeOffset CapturedAt, HardwareCategory[] Categories);

// Explicit property projections only. No methods, serial numbers, MAC/IP addresses or file enumeration.
public static class HardwareInventory
{
    private static readonly (string Key, string Title, string Glyph)[] Categories =
    [ ("cpu", "处理器", "\uE950"), ("gpu", "显示适配器", "\uE7F4"),
      ("memory", "内存", "\uE964"), ("storage", "存储设备", "\uEDA2"),
      ("board", "主板与固件", "\uE977"), ("network", "网络连接", "\uE968"),
      ("audio", "音频设备", "\uE767"), ("system", "操作系统", "\uE770") ];

    public static async Task<HardwareReport> ReadAsync(IProgress<HardwareCategory>? progress = null, CancellationToken cancellationToken = default)
    {
        using var concurrency = new SemaphoreSlim(2);
        var tasks = Categories.Select(async category =>
        {
            await concurrency.WaitAsync(cancellationToken);
            try
            {
                var result = await Task.Run(() => ReadCategory(category.Key, category.Title, category.Glyph), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(result);
                return result;
            }
            finally { concurrency.Release(); }
        }).ToArray();
        return new(DateTimeOffset.Now, await Task.WhenAll(tasks));
    }
    public static string BinarySize(ulong? bytes) => bytes is > 0 ? $"{bytes.Value / 1073741824.0:0.##} GiB" : "未报告";
    public static string DiskSize(ulong? bytes) => bytes is > 0 ? bytes.Value >= 1_000_000_000_000
        ? $"{bytes.Value / 1_000_000_000_000.0:0.##} TB" : $"{bytes.Value / 1_000_000_000.0:0.##} GB" : "未报告";
    private static ulong? N(Dictionary<string, string> row, string key) => row.TryGetValue(key, out var value)
        && ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
    private static string V(Dictionary<string, string> row, string key) => row.TryGetValue(key, out var value)
        && !string.IsNullOrWhiteSpace(value) && value is not ("不可用" or "Not Applicable" or "To be filled by O.E.M.") ? value.Trim() : "未报告";
    private static HardwareProperty P(string label, string value) => new(label, value);
    private static string Count(ulong? value) => value is > 0 ? value.Value.ToString(CultureInfo.InvariantCulture) : "未报告";
    private static string Date(Dictionary<string, string> row, string key)
    {
        var value = V(row, key);
        return value.Length >= 8 && DateTime.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd") : "未报告";
    }
    private static HardwareCategory ReadCategory(string key, string title, string glyph)
    {
        var errors = new List<string>();
        List<Dictionary<string, string>> Q(string sql, string? scope = null) => StaticInventory.Query(sql, errors, scope);
        HardwareDevice[] devices = key switch
        {
            "cpu" => Q("SELECT Name,Manufacturer,NumberOfCores,NumberOfLogicalProcessors,L3CacheSize,SocketDesignation FROM Win32_Processor")
                .Select(r => new HardwareDevice(V(r, "Name"), $"{Count(N(r,"NumberOfCores"))} 核心 · {Count(N(r,"NumberOfLogicalProcessors"))} 线程",
                [P("制造商", V(r,"Manufacturer")), P("核心 / 线程", $"{Count(N(r,"NumberOfCores"))} / {Count(N(r,"NumberOfLogicalProcessors"))}"),
                 P("L3 缓存", N(r,"L3CacheSize") is > 0 ? $"{N(r,"L3CacheSize") / 1024.0:0.##} MiB" : "未报告"), P("插槽标识",V(r,"SocketDesignation"))])).ToArray(),
            "gpu" => Q("SELECT Name,AdapterCompatibility,DriverVersion,DriverDate,PNPDeviceID FROM Win32_VideoController")
                .OrderBy(r => !V(r,"PNPDeviceID").StartsWith("PCI\\",StringComparison.OrdinalIgnoreCase))
                .Select(r => new HardwareDevice(V(r,"Name"), V(r,"AdapterCompatibility"),
                [P("驱动版本",V(r,"DriverVersion")), P("驱动日期",Date(r,"DriverDate"))],
                IsVirtual: V(r,"PNPDeviceID").StartsWith("ROOT\\",StringComparison.OrdinalIgnoreCase)
                    || V(r,"Name").Contains("Virtual",StringComparison.OrdinalIgnoreCase))).ToArray(),
            "memory" => Q("SELECT Capacity,Manufacturer,PartNumber,DeviceLocator,ConfiguredClockSpeed,SMBIOSMemoryType,FormFactor FROM Win32_PhysicalMemory")
                .OrderBy(r => V(r,"DeviceLocator"))
                .Select(r => new HardwareDevice(V(r,"PartNumber"), $"{BinarySize(N(r,"Capacity"))} · {MemoryType(N(r,"SMBIOSMemoryType"))} · {(N(r,"FormFactor") == 12 ? "SO-DIMM" : N(r,"FormFactor") == 8 ? "DIMM" : "内存模块")}",
                [P("制造商",V(r,"Manufacturer")), P("槽位",V(r,"DeviceLocator")), P("配置时钟 (WMI)",N(r,"ConfiguredClockSpeed") is > 0 ? $"{N(r,"ConfiguredClockSpeed")} MHz" : "未报告")],
                CapacityBytes:N(r,"Capacity"))).ToArray(),
            "storage" => ReadStorage(errors),
            "board" => Q("SELECT Manufacturer,Product,Version FROM Win32_BaseBoard")
                .Select(r => new HardwareDevice(V(r,"Product"),"主板", [P("制造商",V(r,"Manufacturer")),P("硬件版本",V(r,"Version"))]))
                .Concat(Q("SELECT Manufacturer,SMBIOSBIOSVersion,ReleaseDate FROM Win32_BIOS")
                    .Select(r => new HardwareDevice(V(r,"SMBIOSBIOSVersion"), "BIOS / UEFI 固件", [P("提供商",V(r,"Manufacturer")),P("发布日期",Date(r,"ReleaseDate"))]))).ToArray(),
            "network" => Q("SELECT Name,Manufacturer,PNPDeviceID FROM Win32_NetworkAdapter WHERE PhysicalAdapter=True")
                .Where(r => V(r,"PNPDeviceID").StartsWith("PCI\\",StringComparison.OrdinalIgnoreCase) || V(r,"PNPDeviceID").StartsWith("USB\\",StringComparison.OrdinalIgnoreCase))
                .Select(r => new HardwareDevice(V(r,"Name"),V(r,"Manufacturer"), [P("系统枚举接口",V(r,"PNPDeviceID").StartsWith("PCI\\",StringComparison.OrdinalIgnoreCase) ? "PCI" : "USB")])).ToArray(),
            "audio" => Q("SELECT Name,Manufacturer FROM Win32_SoundDevice")
                .Select(r => new HardwareDevice(V(r,"Name"), V(r,"Manufacturer"), [], IsVirtual:V(r,"Name").Contains("Virtual",StringComparison.OrdinalIgnoreCase))).ToArray(),
            "system" => Q("SELECT Caption,Version,BuildNumber,OSArchitecture FROM Win32_OperatingSystem")
                .Select(r => new HardwareDevice(V(r,"Caption"), V(r,"OSArchitecture"), [P("系统版本",V(r,"Version")),P("内部版本",V(r,"BuildNumber"))])).ToArray(),
            _ => []
        };
        return new(key, title, glyph, devices, errors.ToArray());
    }
    private static string MemoryType(ulong? type) => type switch { 26 => "DDR4", 34 => "DDR5", 30 => "LPDDR4", 35 => "LPDDR5", _ => type is > 0 ? $"类型 {type}" : "类型未报告" };
    private static HardwareDevice[] ReadStorage(List<string> errors)
    {
        var storageErrors = new List<string>();
        var rows = StaticInventory.Query("SELECT FriendlyName,Size,MediaType,BusType FROM MSFT_PhysicalDisk", storageErrors, @"root\Microsoft\Windows\Storage");
        if (rows.Count > 0)
            return rows.OrderBy(r => N(r,"BusType") == 7).ThenBy(r => V(r,"FriendlyName"))
                .Select(r => new HardwareDevice(V(r,"FriendlyName"), $"{DiskSize(N(r,"Size"))} · {BusType(N(r,"BusType"))} · {MediaType(N(r,"MediaType"))}",
                    [P("物理容量",$"{DiskSize(N(r,"Size"))} / {BinarySize(N(r,"Size"))}")],
                    IsVirtual:N(r,"BusType") == 15, IsExternal:N(r,"BusType") == 7, CapacityBytes:N(r,"Size"))).ToArray();
        // A projected legacy fallback is useful when the Storage provider is unavailable.
        return StaticInventory.Query("SELECT Model,Size,InterfaceType FROM Win32_DiskDrive", errors)
            .Select(r => new HardwareDevice(V(r,"Model"),$"{DiskSize(N(r,"Size"))} · 系统接口 {V(r,"InterfaceType")}",
                [P("物理容量",$"{DiskSize(N(r,"Size"))} / {BinarySize(N(r,"Size"))}")],
                IsExternal:V(r,"InterfaceType")=="USB", CapacityBytes:N(r,"Size"))).ToArray();
    }
    private static string BusType(ulong? type) => type switch { 7 => "USB", 11 => "SATA", 12 => "SD", 13 => "MMC", 15 => "虚拟磁盘", 16 => "存储空间", 17 => "NVMe", 1 => "SCSI", 3 => "ATA", 8 => "RAID", 9 => "iSCSI", 10 => "SAS", _ => "接口未报告" };
    private static string MediaType(ulong? type) => type switch { 3 => "HDD", 4 => "SSD", 5 => "存储级内存", _ => "存储介质" };
}
