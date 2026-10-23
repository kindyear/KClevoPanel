using System.Security.Cryptography;
using System.Text.Json;

namespace KClevoPanel.Core;

public sealed record FanNode(int Temperature, int RawDuty)
{
    public int Percent => (int)Math.Round(RawDuty * 100d / 255);
    public static FanNode FromPercent(int temperature, int percent) => new(temperature, (int)Math.Round(percent / 100d * 255));
}
public sealed record FanCurve(FanNode[] Nodes)
{
    public string? Error => Nodes is not { Length: 4 } ? "曲线必须包含四个节点"
        : Nodes.Any(n => n.Temperature is < 0 or > 100 || n.RawDuty is < 0 or > 255) ? "节点超出范围"
        : Nodes[0].Temperature is < 20 or > 60 || Nodes[0].RawDuty is < 51 or > 255 ? "固件起点不符合预期"
        : Nodes[3].Temperature is < 95 or > 100 || Nodes[3].RawDuty != 255 ? "固件终点不符合预期"
        : Enumerable.Range(1, 3).Any(i => Nodes[i].Temperature <= Nodes[i - 1].Temperature || Nodes[i].RawDuty < Nodes[i - 1].RawDuty) ? "温度必须递增，占空比不能递减" : null;
    public bool Same(FanCurve other) => Nodes.SequenceEqual(other.Nodes);
}
public sealed record FanCurveSnapshot(DateTimeOffset CapturedAt, FanCurve[] Curves, PresetSnapshot Settings)
{
    public string Token => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Curves, Settings.Token })));
    public static FanCurveSnapshot Decode(ThermalReading thermal, PresetSnapshot settings)
    {
        if (!thermal.BoundsIntact || thermal.Rc13 != 13 || thermal.Raw13.Length != 256 || thermal.Raw13[12] != 2)
            throw new InvalidDataException("没有两路有效的固件曲线");
        var curves = Enumerable.Range(0, 2).Select(f => new FanCurve(Enumerable.Range(0, 4)
            .Select(n => new FanNode(thermal.Raw13[16 + 8 * f + 2 * n], thermal.Raw13[17 + 8 * f + 2 * n])).ToArray())).ToArray();
        if (curves.Any(c => c.Error is not null)) throw new InvalidDataException("固件曲线布局异常：" + curves.First(c => c.Error is not null).Error);
        // Slot 3 is absent. Do not create a fictitious third fan or extrapolate its data.
        var absent = thermal.Raw13.Skip(32).Take(8).ToArray();
        if (!absent.All(b => b == 0) && !absent.SequenceEqual(new byte[] { 0, 0, 0, 3, 0, 5, 0, 0 }))
            throw new InvalidDataException("未使用的第三风扇槽与本机已捕获布局不一致");
        return new(thermal.CapturedAt, curves, settings);
    }
}
public static class FanCurveProtocol
{
    public static void Validate(FanCurveSnapshot baseline, FanCurve[] proposed)
    {
        if (baseline.Curves.Length != 2 || proposed.Length != 2) throw new InvalidDataException("仅支持本机两路风扇");
        if (!DirectPresetPlan.Create(baseline.Settings, baseline.Settings.Mode).CanApply || baseline.Settings.FanMode is not (0 or 1 or 6))
            throw new InvalidDataException("当前固件模式不支持曲线试用");
        if (baseline.Settings.TurboFan != 0 || baseline.Settings.FanOffset != 0)
            throw new InvalidDataException("请先通过原厂程序关闭强冷与风扇偏移，再试用曲线");
        for (int f = 0; f < 2; f++)
        {
            var original = baseline.Curves[f]; var next = proposed[f];
            if (original.Error is { } oldError) throw new InvalidDataException(oldError);
            if (next.Error is { } error) throw new InvalidDataException(error);
            if (next.Nodes[0] != original.Nodes[0] || next.Nodes[3] != original.Nodes[3]) throw new InvalidDataException("固件首尾节点不可修改");
            if (next.Nodes[1].Percent < 30 || next.Nodes[1].Temperature > 90 || next.Nodes[2].Temperature > 95)
                throw new InvalidDataException("可调节点至少 30%，中间温度不超过 90 / 95°C");
        }
    }
    // Native FanSpeedSetting 6.40: FAN.Write_WMI14 -> SetWMIPackage(14), not Ex.
    // 256 bytes; raw duties; three big-endian slopes per fan. No page-4 curve mirror.
    public static byte[] Frame(FanCurve[] curves)
    {
        if (curves.Length != 2 || curves.Any(c => c.Error is not null)) throw new InvalidDataException("曲线无效");
        var frame = new byte[256];
        for (int f = 0; f < 2; f++)
        {
            var nodes = curves[f].Nodes;
            for (int n = 1; n <= 2; n++)
            { frame[2 + 4 * f + 2 * (n - 1)] = (byte)nodes[n].Temperature; frame[3 + 4 * f + 2 * (n - 1)] = (byte)nodes[n].RawDuty; }
            for (int n = 0; n < 3; n++)
            {
                int slope = (int)Math.Round((nodes[n + 1].Percent - nodes[n].Percent) / (double)(nodes[n + 1].Temperature - nodes[n].Temperature) * 2.55 * 16);
                frame[14 + f * 6 + n * 2] = (byte)(slope >> 8); frame[15 + f * 6 + n * 2] = (byte)slope;
            }
        }
        return frame;
    }
    public static bool Matches(FanCurveSnapshot snapshot, FanCurve[] expected) => snapshot.Curves.Length == 2 && expected.Length == 2
        && Enumerable.Range(0, 2).All(f => snapshot.Curves[f].Same(expected[f]));
}
public sealed record FanTrialRequest(FanCurve[] Curves, string ExpectedToken, int AppPid, long AppStartTicks, bool Continuous = false, int TargetMode = 6);
public sealed record FanTrialStatus(string State, string Message, DateTimeOffset? Deadline = null, bool? CurveRestored = null, bool? ModeRestored = null,
    int? CurveReturn = null, int? ModeReturn = null, int? MirrorReturn = null, string? Verification = null);
public sealed record FanModeRequest(int Mode, string ExpectedToken);
public sealed record FanModeResult(bool Confirmed, string Message, FanCurveSnapshot? After, int? CommandReturn = null, int? MirrorReturn = null, bool? RolledBack = null);
