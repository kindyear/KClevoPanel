namespace KClevoPanel.Core;

public static class FanModeTransaction
{
    public static FanModeResult Apply(IFanCurveTransport transport, int mode, string token, Action<string, object> journal)
    {
        if (mode is not (0 or 1)) return new(false, "仅允许自动或最大风量", null);
        var before = transport.ReadFanCurves();
        FanCurveProtocol.Validate(before, before.Curves);
        if (before.Token != token) return new(false, "设置已变化，请重新读取", before);
        if (before.Settings.FanMode == mode) return new(true, "当前已是目标模式", before);
        _ = transport.ReadThermals();
        journal("Prepared", new { Before = before, Mode = mode });
        int? rc = null, saved = null;
        try
        {
            rc = transport.SetFanMode(mode);
            journal("CommandReturned", new { Return = rc });
            if (rc != 121) throw new IOException("风扇模式应答异常");
            saved = transport.SaveFanMode(mode);
            var after = transport.ReadFanCurves();
            if (saved != 1 || after.Settings.FanMode != mode
                || after.Settings.Mode != before.Settings.Mode || after.Settings.Dtt != before.Settings.Dtt
                || after.Settings.TurboFan != before.Settings.TurboFan || after.Settings.FanOffset != before.Settings.FanOffset)
                throw new IOException("风扇模式保存或相关设置核对失败");
            journal("Completed", new { After = after, CommandReturn = rc, MirrorReturn = saved });
            return new(true, "固件模式应答和模式记录已核对", after, rc, saved);
        }
        catch (Exception error)
        {
            bool restored = false;
            try
            {
                // Only restore the captured mode; do not rewrite a curve on a mode-only failure.
                restored = transport.SetFanMode(before.Settings.FanMode) == 121 && transport.SaveFanMode(before.Settings.FanMode) == 1
                    && transport.ReadFanCurves().Settings.Token == before.Settings.Token;
            }
            catch { }
            journal(restored ? "RolledBack" : "RecoveryFailed", new { Error = error.Message, Restored = restored });
            return new(false, error.Message + (restored ? "；原模式已恢复" : "；恢复未确认"), null, rc, saved, restored);
        }
    }
}
