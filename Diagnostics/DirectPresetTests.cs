using KClevoPanel.Core;

internal static class DirectPresetTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var baseline = new PresetSnapshot(DateTimeOffset.Now, 2, 0, 0, 0, 0);
        var plan = DirectPresetPlan.Create(baseline, 3);
        check(plan.CanApply && plan.Payload.SequenceEqual(new byte[] { 3, 0, 0, 25 })
            && plan.RestorePayload.SequenceEqual(new byte[] { 2, 0, 0, 25 }), "Direct mode uses the exact OEM 4-byte payload and baseline");
        check(DirectPresetPlan.Create(baseline with { TurboFan = 1 }, 2).Payload[0] == 0x42, "Performance preserves verified turbo bit 0x40");
        check(DirectPresetPlan.Create(baseline with { TurboFan = 1 }, 0).Payload[0] == 0, "Turbo flag is not copied into Quiet");
        check(!DirectPresetPlan.Create(baseline with { Dtt = 1 }, 3).CanApply, "Unresolved DTT branch blocks writes and unsafe restoration");
        check(!DirectPresetPlan.Create(baseline with { Mode = 4 }, 0).CanApply, "Quiet-plus baseline cannot be silently flattened");
        check(!DirectPresetPlan.Create(baseline, -1).CanApply && !DirectPresetPlan.Create(baseline, 4).CanApply, "No arbitrary firmware mode can be planned");
        check(!DirectPresetPlan.Create(baseline with { TurboFan = 2 }, 3).CanApply, "Unknown turbo layout blocks writes");
        check(baseline.Token == (baseline with { CapturedAt = baseline.CapturedAt.AddSeconds(1) }).Token
            && baseline.Token != (baseline with { FanMode = 1 }).Token, "Precondition token ignores time but detects changed settings");
        var steps = new List<string>();
        var fake = new FakePresetTransport(baseline, steps);
        var applied = DirectPresetTransaction.Apply(fake, 3, baseline.Token, (stage, _) => steps.Add("journal:" + stage));
        check(applied.Accepted && applied.After?.Mode == 3 && fake.Sets == 1 && fake.Saves == 1, "Direct transaction acknowledges one command and one saved selection");
        check(steps.IndexOf("journal:Prepared") < steps.IndexOf("firmware:3")
            && steps.IndexOf("firmware:3") < steps.IndexOf("mirror:3"), "Durable intent precedes firmware and mirror writes");
        check(applied.Verification == "CommandAcknowledgementAndSavedSelection", "Mirror readback is never labeled hardware-effect verification");
        fake = new(baseline, []);
        var stale = DirectPresetTransaction.Apply(fake, 3, "stale", (_, _) => { });
        check(!stale.Accepted && fake.Sets == 0 && fake.Saves == 0, "Stale baseline rejects every write");
        var same = DirectPresetTransaction.Apply(fake, 2, baseline.Token, (_, _) => { });
        check(same.Accepted && !same.WriteAttempted && same.Verification == "SavedSelectionOnly" && fake.Sets == 0,
            "Current mode is a no-op and cannot claim a firmware acknowledgement");
        bool journalFailed = false;
        try { DirectPresetTransaction.Apply(fake, 3, baseline.Token, (_, _) => throw new IOException()); } catch (IOException) { journalFailed = true; }
        check(journalFailed && fake.Sets == 0, "Unavailable recovery journal prevents the first write");
        fake = new(baseline, []) { FailFirstMirror = true };
        var failed = DirectPresetTransaction.Apply(fake, 3, baseline.Token, (_, _) => { });
        check(!failed.Accepted && failed.RestoreSucceeded == true && fake.Sets == 2 && fake.Saves == 2
            && fake.Current.Mode == 2, "Mirror failure restores the validated baseline once");
        fake = new(baseline, []) { FailAllFirmware = true };
        failed = DirectPresetTransaction.Apply(fake, 3, baseline.Token, (_, _) => { });
        check(!failed.Accepted && failed.RestoreSucceeded == false && fake.Sets == 2,
            "Ambiguous firmware result does not loop or claim restoration");
        using var client = new DirectPresetClient();
        check(!(await client.ChangeAsync(-1)).Available && !(await client.ChangeAsync(4)).Available,
            "Direct IPC client rejects out-of-range modes without a child");
        check(!(await client.ChangeAsync(3)).Available, "Direct client cannot apply before a baseline read");
        client.Dispose();
        check(!(await client.ReadAsync()).Available, "Disposed direct client cannot launch a child");
    }
    private sealed class FakePresetTransport(PresetSnapshot initial, List<string> steps) : IDirectPresetTransport
    {
        public PresetSnapshot Current = initial;
        public int Sets, Saves;
        public bool FailFirstMirror, FailAllFirmware;
        public PresetSnapshot Read() => Current;
        public int SetFirmwareMode(byte[] payload) { Sets++; steps.Add("firmware:" + payload[0]); return FailAllFirmware ? 0 : 121; }
        public int SaveSelection(int mode)
        {
            Saves++; steps.Add("mirror:" + mode);
            if (FailFirstMirror && Saves == 1) return 0;
            Current = Current with { Mode = mode }; return 1;
        }
    }
}
