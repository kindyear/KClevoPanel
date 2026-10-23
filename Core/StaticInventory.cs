using System.Diagnostics;
using System.Management;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace KClevoPanel.Core;

public static class StaticInventory
{
    public static string ReadCpuNameFast()
    {
        try { return Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null)?.ToString() ?? "型号读取中"; }
        catch { return "型号读取中"; }
    }
    private static DriverInfo[] BridgeDrivers(List<string> errors) =>
        Query("SELECT DeviceID,DeviceName,DriverVersion,InfName FROM Win32_PnPSignedDriver WHERE DeviceID LIKE 'ACPI%CLV0001%' OR DeviceID LIKE 'ACPI%CLV0002%'", errors)
            .Where(x => Value(x, "DeviceID").StartsWith("ACPI\\CLV0001\\", StringComparison.OrdinalIgnoreCase)
                     || Value(x, "DeviceID").StartsWith("ACPI\\CLV0002\\", StringComparison.OrdinalIgnoreCase))
            .Select(x => new DriverInfo(Value(x, "DeviceName"), Value(x, "DriverVersion"), Value(x, "InfName"))).ToArray();
    public static FirmwareIdentity ReadFirmwareIdentity()
    {
        var first = ReadFirmwareIdentityOnce();
        if (first.Errors.Length == 0) return first;
        // A provider can yield valid rows and then fail enumeration during concurrent startup.
        // Retry the entire read once, discarding partial rows/errors. Never cache or relax identity guards.
        Thread.Sleep(250);
        return ReadFirmwareIdentityOnce();
    }
    private static FirmwareIdentity ReadFirmwareIdentityOnce()
    {
        var errors = new List<string>();
        var computer = Query("SELECT Model FROM Win32_ComputerSystem", errors).FirstOrDefault();
        var bios = Query("SELECT SMBIOSBIOSVersion FROM Win32_BIOS", errors).FirstOrDefault();
        return new(Value(computer, "Model"), Value(bios, "SMBIOSBIOSVersion"), BridgeDrivers(errors), errors.ToArray());
    }
    public static MachineInfo Read()
    {
        var errors = new List<string>();
        // Limit WMI concurrency and keep per-query errors, without changing control identity checks.
        string[] queries = ["SELECT Manufacturer,Model FROM Win32_ComputerSystem",
            "SELECT SMBIOSBIOSVersion FROM Win32_BIOS", "SELECT Caption,Version,BuildNumber FROM Win32_OperatingSystem",
            "SELECT Name FROM Win32_VideoController"];
        var rows = new List<Dictionary<string,string>>[queries.Length];
        var queryErrors = new List<string>[queries.Length];
        Parallel.For(0, queries.Length, new ParallelOptions { MaxDegreeOfParallelism = 2 }, i =>
        { queryErrors[i] = []; rows[i] = Query(queries[i], queryErrors[i]); });
        foreach (var list in queryErrors) errors.AddRange(list);
        var computer = rows[0].FirstOrDefault(); var bios = rows[1].FirstOrDefault();
        var os = rows[2].FirstOrDefault(); var gpus = rows[3].Select(x => Value(x, "Name")).ToArray();
        string cpu = ReadCpuNameFast();
        if (cpu == "型号读取中") cpu = Value(Query("SELECT Name FROM Win32_Processor", errors).FirstOrDefault(), "Name");
        var drivers = BridgeDrivers(errors);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ControlCenter");
        var libraries = new List<DllInfo> { ReadDll(Path.Combine(root, "InsydeDCHU.dll")), ReadDll(Path.Combine(root, "DCHU", "InsydeDCHU.dll")) };
        try
        {
            var active = TargetDchuLibrary.Resolve();
            if (!libraries.Any(d => string.Equals(d.Path, active.Path, StringComparison.OrdinalIgnoreCase))) libraries.Add(active);
        }
        catch { }
        return new(Value(computer, "Manufacturer"), Value(computer, "Model"), Value(bios, "SMBIOSBIOSVersion"),
            $"{Value(os, "Caption")} · {Value(os, "Version")}", cpu, gpus, drivers,
            libraries.ToArray(),
            DateTimeOffset.Now, errors.ToArray());
    }
    private static string Value(Dictionary<string,string>? row, string key) =>
        row is not null && row.TryGetValue(key, out string? value) ? value : "不可用";
    internal static List<Dictionary<string,string>> Query(string sql, List<string> errors, string? scope = null)
    {
        var result = new List<Dictionary<string,string>>();
        try
        {
            using var search = scope is null ? new ManagementObjectSearcher(sql) : new ManagementObjectSearcher(scope, sql);
            search.Options.Timeout = TimeSpan.FromSeconds(8);
            using var rows = search.Get();
            foreach (ManagementObject row in rows)
            {
                using (row)
                {
                    var values = new Dictionary<string,string>();
                    foreach (PropertyData property in row.Properties) values[property.Name] = property.Value?.ToString() ?? "不可用";
                    result.Add(values);
                }
            }
        }
        catch (Exception e) { errors.Add($"系统信息查询失败：{e.GetType().Name}：{e.Message}"); }
        return result;
    }
    public static DllInfo ReadDll(string path)
    {
        try
        {
            byte[] data = File.ReadAllBytes(path);
            using var reader = new PEReader(new MemoryStream(data, writable:false));
            if (reader.PEHeaders.CoffHeader.Machine != Machine.Amd64) throw new InvalidDataException("DLL 不是 x64");
            string[] names = ExportNames(data, reader.PEHeaders);
            return new(path, data.LongLength, Convert.ToHexString(SHA256.HashData(data)), names, null);
        }
        catch (Exception e) { return new(path, null, null, [], e.Message); }
    }
    private static string[] ExportNames(byte[] data, PEHeaders headers)
    {
        int Offset(int rva)
        {
            foreach (var section in headers.SectionHeaders)
                if (rva >= section.VirtualAddress && (long)rva < (long)section.VirtualAddress + section.SizeOfRawData)
                    return checked(section.PointerToRawData + rva - section.VirtualAddress);
            throw new InvalidDataException("无效 PE RVA");
        }
        int Int(int offset) => BitConverter.ToInt32(data, offset);
        var directory = headers.PEHeader!.ExportTableDirectory;
        if (directory.RelativeVirtualAddress == 0) return [];
        int table = Offset(directory.RelativeVirtualAddress), count = Int(table+24), names = Offset(Int(table+32));
        if (count < 0 || count > 65536) throw new InvalidDataException("无效导出数量");
        var result = new List<string>();
        for (int i=0; i<count; i++)
        {
            int start = Offset(Int(checked(names+i*4))), end = Array.IndexOf(data, (byte)0, start);
            if (end < start) throw new InvalidDataException("无效导出名称");
            result.Add(Encoding.ASCII.GetString(data, start, end-start));
        }
        return result.ToArray();
    }
}
