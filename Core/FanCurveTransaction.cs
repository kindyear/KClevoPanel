namespace KClevoPanel.Core;

public interface IFanCurveTransport
{
    FanCurveSnapshot ReadFanCurves();
    ThermalReading ReadThermals();
    int SetFanCurve(FanCurve[] curves);
    int SetFanMode(int mode);
    int SaveFanMode(int mode);
}
public sealed record FanRestoreResult(bool CurveRestored, bool ModeRestored, string? Error, int? CurveReturn = null, int? ModeReturn = null, int? MirrorReturn = null);
public static class FanCurveTransaction
{
    public static void RequireCool(ThermalReading t, bool initial)
    {
        if (!t.BoundsIntact || t.CpuTemperatureC is null || t.GpuTemperatureC is null || t.Fans.Length != 2
            || t.CpuTemperatureC >= (initial ? 90 : 95) || t.GpuTemperatureC >= (initial ? 82 : 87)
            || t.Fans.Any(f => !f.Rpm.HasValue)
            || t.Fans[0].Rpm == 0 && t.CpuTemperatureC >= 55
            || t.Fans[1].Rpm == 0 && t.GpuTemperatureC >= 50)
            throw new IOException("温度、转速或遥测状态未满足控制条件");
    }
    // The caller must already hold the transaction mutex and have armed a durable independent guardian.
    public static int? Apply(IFanCurveTransport transport, FanCurveSnapshot baseline, FanCurve[] proposed, Action<int> delay, bool allowExisting = false)
    {
        FanCurveProtocol.Validate(baseline, proposed);
        bool unchanged = FanCurveProtocol.Matches(baseline, proposed);
        if (unchanged && !allowExisting) throw new IOException("请先修改至少一个中间节点；相同曲线无法验证写入效果");
        if (transport.ReadFanCurves().Token != baseline.Token) throw new IOException("写入前状态发生变化，已取消");
        RequireCool(transport.ReadThermals(), true);
        int? curveReturn = null;
        if (!unchanged)
        {
            curveReturn = transport.SetFanCurve(proposed);
            delay(300);
            // This DLL returns ACPI integers as well as command echoes. Runtime state is the criterion.
            if (!FanCurveProtocol.Matches(transport.ReadFanCurves(), proposed))
                throw new IOException($"曲线运行时节点不匹配（返回 {curveReturn}），未启用自定义模式");
        }
        if (transport.SetFanMode(6) != 121 || transport.SaveFanMode(6) != 1) throw new IOException("自定义模式请求未确认");
        delay(400);
        var after = transport.ReadFanCurves();
        if (!FanCurveProtocol.Matches(after, proposed) || after.Settings.FanMode != 6)
            throw new IOException("两路运行时曲线或自定义模式复核未通过");
        return curveReturn;
    }
    public static FanRestoreResult Restore(IFanCurveTransport transport, FanCurveSnapshot baseline, Action<int> delay)
    {
        bool curveOk = false, modeOk = false;
        int? curveRc = null, modeRc = null, mirrorRc = null;
        var errors = new List<string>();
        try
        {
            curveOk = FanCurveProtocol.Matches(transport.ReadFanCurves(), baseline.Curves);
            if (!curveOk)
            {
                curveRc = transport.SetFanCurve(baseline.Curves); delay(400);
                curveOk = FanCurveProtocol.Matches(transport.ReadFanCurves(), baseline.Curves);
            }
            if (!curveOk) errors.Add("原曲线未确认");
        }
        catch (Exception error) { errors.Add(error.Message); }
        // Even if the table command fails, independently attempt to relinquish Custom mode.
        int mode = curveOk ? baseline.Settings.FanMode : 0;
        try
        {
            modeOk = transport.ReadFanCurves().Settings.FanMode == mode;
            if (!modeOk)
            {
                modeRc = transport.SetFanMode(mode); mirrorRc = transport.SaveFanMode(mode);
                modeOk = modeRc == 121 && mirrorRc == 1 && transport.ReadFanCurves().Settings.FanMode == mode;
            }
            if (!modeOk) errors.Add("风扇模式恢复未确认");
        }
        catch (Exception error) { errors.Add(error.Message); }
        return new(curveOk, modeOk, errors.Count == 0 ? null : string.Join("；", errors), curveRc, modeRc, mirrorRc);
    }
}
