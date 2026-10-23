using System.Security.Cryptography;
using System.Text;

namespace KClevoPanel.Core;

public sealed record PresetSnapshot(DateTimeOffset CapturedAt, int Mode, int Dtt, int TurboFan, int FanMode, int FanOffset)
{
    // Temperatures/time must not make an otherwise identical precondition stale.
    public string Token => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"X17-1.07.04TCOLO-v1:{Mode}:{Dtt}:{TurboFan}:{FanMode}:{FanOffset}")));
}

public sealed record DirectPresetPlan(int Target, bool CanApply, string? Reason, byte[] Payload, byte[] RestorePayload,
    string ExpectedToken, string Scope = "FirmwareModeOnly")
{
    public static DirectPresetPlan Create(PresetSnapshot before, int target)
    {
        string? reason = target is < 0 or > 3 ? "档位不在0至3白名单中"
            : before.Mode is < 0 or > 3 ? "当前档位不是已核对的四档状态"
            : before.Dtt != 0 ? "DTT标志已启用或未知，需要核对本机DTT分支后才能写入"
            : before.TurboFan is < 0 or > 1 ? "增压标志不符合已核对布局"
            : before.FanMode is < 0 or > 9 || before.FanOffset is < 0 or > 255 ? "风扇设置布局异常" : null;
        return new(target, reason is null, reason, reason is null ? PayloadFor(target, before.TurboFan) : [],
            reason is null ? PayloadFor(before.Mode, before.TurboFan) : [], before.Token);
    }
    private static byte[] PayloadFor(int mode, int turbo)
        => [(byte)(mode == 2 && turbo == 1 ? mode | 0x40 : mode), 0, 0, 25];
}

// The production implementation only exposes fixed settings and command 121/subcommand 25.
public interface IDirectPresetTransport
{
    PresetSnapshot Read();
    int SetFirmwareMode(byte[] payload);
    int SaveSelection(int mode);
}

public sealed record DirectPresetResult(bool Accepted, bool WriteAttempted, string Status, PresetSnapshot? After,
    int? FirmwareReturn, int? MirrorReturn, bool RestoreAttempted, bool? RestoreSucceeded,
    string Verification = "Unconfirmed");

public static class DirectPresetTransaction
{
    public static DirectPresetResult Apply(IDirectPresetTransport transport, int target, string expectedToken,
        Action<string, object> journal)
    {
        var before = transport.Read();
        var plan = DirectPresetPlan.Create(before, target);
        if (!plan.CanApply) return new(false, false, plan.Reason!, before, null, null, false, null);
        if (expectedToken != before.Token) return new(false, false, "档位或设置已变化，请等待刷新后重试", before, null, null, false, null);
        if (before.Mode == target) return new(true, false, "档位未变化", before, null, null, false, null, "SavedSelectionOnly");
        // Durable intent exists before the first write. Never accept arbitrary commands from a journal.
        journal("Prepared", new { Before = before, Plan = plan });
        int? rc = null, mirror = null;
        bool attempted = false;
        try
        {
            attempted = true;
            rc = transport.SetFirmwareMode(plan.Payload);
            if (rc != 121) throw new IOException("固件模式命令未返回已核对的应答");
            journal("FirmwareAcknowledged", new { Return = rc });
            mirror = transport.SaveSelection(target);
            if (mirror != 1) throw new IOException("档位记录保存失败");
            var after = transport.Read();
            if (after.Mode != target || after.Dtt != before.Dtt || after.TurboFan != before.TurboFan
                || after.FanMode != before.FanMode || after.FanOffset != before.FanOffset)
                throw new IOException("设置读回不一致或存在并发改动");
            journal("Completed", new { After = after, FirmwareReturn = rc, MirrorReturn = mirror,
                Verification = "CommandAcknowledgementAndSavedSelection", HardwareEffectVerified = false });
            return new(true, true, "模式命令已应答，档位记录已同步；硬件效果未校验", after, rc, mirror, false, null,
                "CommandAcknowledgementAndSavedSelection");
        }
        catch (Exception error)
        {
            if (!attempted) throw;
            bool restored = false;
            PresetSnapshot? after = null;
            try
            {
                // One compensating request using the already validated baseline, never an automatic retry of the target.
                int restoreRc = transport.SetFirmwareMode(plan.RestorePayload);
                int restoreMirror = restoreRc == 121 ? transport.SaveSelection(before.Mode) : 0;
                after = transport.Read();
                restored = restoreRc == 121 && restoreMirror == 1 && after.Token == before.Token;
            }
            catch { }
            try { journal("Failed", new { Error = error.Message, RestoreAttempted = true, RestoreSucceeded = restored, After = after }); }
            catch { }
            return new(false, true, restored ? "切换未完成，已发送原档位恢复命令并恢复记录"
                : "切换结果未确认，原档位恢复失败；请查看恢复日志", after, rc, mirror, true, restored,
                restored ? "BaselineCommandAcknowledgementAndSavedSelection" : "Unconfirmed");
        }
    }
}
