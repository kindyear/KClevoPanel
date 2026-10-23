namespace KClevoPanel.Core;

public static class TargetDchuLibrary
{
    public const string DriverStoreHash = "470D96CAE14FEF2823330C63EA683CF87CB0F6984DC4EB86D21420FF71BE3299";
    public const string LegacyDchuHash = "D385DEF14C5AA6CBB8EB60FD34D0EFEB92EAF39E875AB28DE05FE0FDAA7CAB9C";
    public static DllInfo Resolve()
    {
        var store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "DriverStore", "FileRepository");
        if (Directory.Exists(store))
        {
            foreach (string directory in Directory.EnumerateDirectories(store, "acpibridge1.inf_amd64_*").Order())
            {
                var dll = StaticInventory.ReadDll(Path.Combine(directory, "InsydeDCHU.dll"));
                if (dll.Error is null && dll.Sha256 == DriverStoreHash) return dll;
            }
        }
        string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ControlCenter", "DCHU", "InsydeDCHU.dll");
        var fallback = StaticInventory.ReadDll(legacy);
        if (fallback.Error is null && fallback.Sha256 == LegacyDchuHash) return fallback;
        throw new InvalidOperationException("未找到已核对的驱动仓库或DCHU接口库");
    }
}
