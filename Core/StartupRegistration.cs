using Microsoft.Win32;

namespace KClevoPanel.Core;

public static class StartupRegistration
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "KClevoPanel";
    public static string Command(string path) => "\"" + Path.GetFullPath(path) + "\" --startup";
    public static bool IsEnabled(string path)
    { using var key = Registry.CurrentUser.OpenSubKey(KeyPath); return string.Equals(key?.GetValue(Name) as string, Command(path), StringComparison.OrdinalIgnoreCase); }
    public static void SetEnabled(string path, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
        if (enabled) key.SetValue(Name, Command(path), RegistryValueKind.String);
        else key.DeleteValue(Name, false);
        if (IsEnabled(path) != enabled) throw new IOException("启动项保存后核对失败");
    }
}
