using System.Text.RegularExpressions;
using KClevoPanel.Core;

namespace KClevoPanel.App;

internal static class DeviceLabels
{
    internal static string CpuModel(string raw)
    {
        string name = Regex.Replace(raw, @"^\d+(st|nd|rd|th) Gen\s+", "", RegexOptions.IgnoreCase);
        return Regex.Replace(name.Replace("(R)", "").Replace("(TM)", ""), @"\s+", " ").Trim();
    }
    internal static string GpuModel(string? raw) => raw is null ? "型号读取中" : raw.Replace("NVIDIA GeForce ", "");
    internal static string Temperature(double? value) => value.HasValue ? $"{value:0} °C" : "— °C";
    internal static string GpuDetails(GpuReading gpu) => $"功耗 {gpu.Watts?.ToString("0.0") ?? "—"} W · 频率 {gpu.ClockMhz?.ToString("0") ?? "—"} MHz";
}
