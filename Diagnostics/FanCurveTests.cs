using KClevoPanel.Core;

internal static class FanCurveTests
{
    public static void Run(Action<bool, string> check)
    {
        FanCurve Make(int end) => new([new(40, 71), new(60, 133), new(80, 194), new(end, 255)]);
        var baseline = new FanCurveSnapshot(DateTimeOffset.Now, [Make(100), Make(99)], new(DateTimeOffset.Now, 2, 0, 0, 0, 0));
        var proposed = baseline.Curves.Select(c => new FanCurve([c.Nodes[0], FanNode.FromPercent(62, 55), FanNode.FromPercent(82, 80), c.Nodes[3]])).ToArray();
        var frame = FanCurveProtocol.Frame(proposed);
        check(frame.Length == 256 && frame[2] == 62 && frame[3] == 140 && frame[4] == 82 && frame[5] == 204, "Fan frame uses raw duty with exact native rounding");
        check(frame[14] == 0 && frame[15] == 50 && frame[16] == 0 && frame[17] == 51 && frame[18] == 0 && frame[19] == 45, "Fan slope bytes follow native percent formula and big endian layout");
        check(frame[24] == 0 && frame[25] == 48, "GPU slope preserves this device's 99C endpoint");
        check(frame.Skip(10).Take(4).All(v => v == 0) && frame.Skip(26).All(v => v == 0), "Absent third fan and all reserved bytes stay zero");
        var data = new byte[256]; data[12] = 2;
        for (int f = 0; f < 2; f++) for (int n = 0; n < 4; n++) { data[16+f*8+n*2] = (byte)baseline.Curves[f].Nodes[n].Temperature; data[17+f*8+n*2] = (byte)baseline.Curves[f].Nodes[n].RawDuty; }
        data[35] = 3; data[37] = 5;
        var thermal = new ThermalReading(DateTimeOffset.Now, 50, 42, [new("CPU", 3000, 50, 700), new("GPU", 2800, 50, 760)], "test", 12, 13, new byte[256], data, true, 1);
        check(FanCurveSnapshot.Decode(thermal, baseline.Settings).Curves[1].Nodes[3].Temperature == 99, "Runtime decoder reads all nodes without inventing endpoint temperatures");
        bool Reject(Action action) { try { action(); return false; } catch { return true; } }
        data[35] = data[37] = 0;
        check(FanCurveSnapshot.Decode(thermal, baseline.Settings).Curves.Length == 2, "Normalized all-zero absent slot is accepted without inventing a fan");
        data[32] = 40;
        check(Reject(() => FanCurveSnapshot.Decode(thermal, baseline.Settings)), "An unknown active third slot remains rejected"); data[32] = 0;
        var changedEndpoint = proposed.Select(c => new FanCurve(c.Nodes.ToArray())).ToArray(); changedEndpoint[1].Nodes[3] = new(100, 255);
        check(Reject(() => FanCurveProtocol.Validate(baseline, changedEndpoint)), "Changed firmware endpoints are rejected");
        var crossed = proposed.Select(c => new FanCurve(c.Nodes.ToArray())).ToArray(); crossed[0].Nodes[1] = new(90, 140);
        check(Reject(() => FanCurveProtocol.Validate(baseline, crossed)), "Crossed temperatures are rejected");
        var tooSlow = proposed.Select(c => new FanCurve(c.Nodes.ToArray())).ToArray(); tooSlow[0].Nodes[1] = FanNode.FromPercent(62, 29);
        check(Reject(() => FanCurveProtocol.Validate(baseline, tooSlow)), "Duty below 30 percent is rejected");
        check(Reject(() => FanCurveProtocol.Validate(baseline with { Settings = baseline.Settings with { FanOffset = 1 } }, proposed))
            && Reject(() => FanCurveProtocol.Validate(baseline with { Settings = baseline.Settings with { TurboFan = 1 } }, proposed))
            && Reject(() => FanCurveProtocol.Validate(baseline with { Settings = baseline.Settings with { FanMode = 3 } }, proposed)), "Offsets, turbo fan and unverified fan modes block trial");
        var otherMode = baseline with { Settings = baseline.Settings with { Mode = 3 } };
        check(otherMode.Token != baseline.Token && baseline.Token == (baseline with { CapturedAt = DateTimeOffset.Now.AddSeconds(1) }).Token, "Precondition token tracks settings and ignores timestamps");
        var fake = new Fake(baseline, thermal);
        check(Reject(() => FanCurveTransaction.Apply(fake, baseline, baseline.Curves, _ => { })) && fake.Writes == 0, "Unchanged curves do not issue writes or claim a hardware verification");
        FanCurveTransaction.Apply(fake, baseline, proposed, _ => { });
        check(fake.Writes == 3 && FanCurveProtocol.Matches(fake.Current, proposed) && fake.Current.Settings.FanMode == 6, "Trial applies both curves and custom selection only after valid preconditions");
        var restored = FanCurveTransaction.Restore(fake, baseline, _ => { });
        check(restored.CurveRestored && restored.ModeRestored && fake.Current.Settings.FanMode == 0, "Guardian restoration verifies both tables and saved mode");
        var stale = new Fake(otherMode, thermal);
        check(Reject(() => FanCurveTransaction.Apply(stale, baseline, proposed, _ => { })) && stale.Writes == 0, "Stale settings produce zero native writes");
        var hot = new Fake(baseline, thermal with { CpuTemperatureC = 95 });
        check(Reject(() => FanCurveTransaction.Apply(hot, baseline, proposed, _ => { })) && hot.Writes == 0, "Unsafe initial temperatures produce zero native writes");
        var ackOnly = new Fake(baseline, thermal) { IgnoreCurveWrite = true };
        check(Reject(() => FanCurveTransaction.Apply(ackOnly, baseline, proposed, _ => { })), "Command echo and mode mirror alone cannot report curve success");
        var damaged = new Fake(baseline, thermal) { CorruptGpu = true };
        check(Reject(() => FanCurveTransaction.Apply(damaged, baseline, proposed, _ => { })), "A mismatch in GPU nodes rejects whole trial");
        var failedRestore = new Fake(baseline with { Curves = proposed, Settings = baseline.Settings with { FanMode = 6 } }, thermal) { ThrowCurve = true };
        restored = FanCurveTransaction.Restore(failedRestore, baseline, _ => { });
        check(!restored.CurveRestored && restored.ModeRestored && failedRestore.ModeRequests.SequenceEqual(new[] { 0 }), "Failed curve restoration still attempts Auto independently");
        check(Reject(() => FanCurveTransaction.RequireCool(thermal with { GpuTemperatureC = 87 }, false))
            && Reject(() => FanCurveTransaction.RequireCool(thermal with { CpuTemperatureC = 70, Fans = [new("CPU", 0, 0, 0), thermal.Fans[1]] }, false)), "Overtemperature and hot stopped fan interrupt trial");
        FanCurveTransaction.RequireCool(thermal with { CpuTemperatureC = 40, GpuTemperatureC = 30, Fans = [new("CPU", 0, 0, 0), new("GPU", 0, 0, 0)] }, false);
        check(true, "Cold firmware-controlled fan stop differs from an invalid RPM sample");
        var acpiReturn = new Fake(baseline, thermal) { CurveReturn = 20 };
        check(FanCurveTransaction.Apply(acpiReturn, baseline, proposed, _ => { }) == 20, "ACPI integer return is recorded and runtime nodes determine curve success");
        var noRecoveryNeeded = new Fake(baseline, thermal);
        var verified = FanCurveTransaction.Restore(noRecoveryNeeded, baseline, _ => { });
        check(verified.CurveRestored && verified.ModeRestored && noRecoveryNeeded.Writes == 0, "Already-restored state is reconciled without any writes");
        var existingCurve = new Fake(baseline, thermal);
        FanCurveTransaction.Apply(existingCurve, baseline, baseline.Curves, _ => { }, true);
        check(existingCurve.Writes == 2 && existingCurve.Current.Settings.FanMode == 6, "Continuous control can select an existing curve without rewriting its table");
        var modes = new Fake(baseline, thermal); var stages = new List<string>();
        var modeResult = FanModeTransaction.Apply(modes, 1, baseline.Token, (s, _) => stages.Add(s));
        check(modeResult.Confirmed && modes.Writes == 2 && stages[0] == "Prepared" && stages[^1] == "Completed", "Mode-only transaction journals intent and writes only mode and mirror");
        check(!FanModeTransaction.Apply(modes, 7, modes.Current.Token, (_, _) => { }).Confirmed && modes.Writes == 2, "Unknown fan modes issue zero writes");
        var noModeChange = new Fake(baseline, thermal);
        check(FanModeTransaction.Apply(noModeChange, 0, baseline.Token, (_, _) => { }).Confirmed && noModeChange.Writes == 0, "Auto already selected is a read-only no-op");
        var staleMode = new Fake(baseline, thermal);
        check(!FanModeTransaction.Apply(staleMode, 1, otherMode.Token, (_, _) => { }).Confirmed && staleMode.Writes == 0, "Mode preconditions reject stale tokens");
        var time = DateTimeOffset.UtcNow;
        check(FanControlLifetime.StopReason(time.AddSeconds(30), time, false, null, true, false, time.AddSeconds(30)) is not null, "Trials expire after 30 seconds");
        check(FanControlLifetime.StopReason(time.AddMinutes(30), time, true, time.AddMinutes(30), true, false, time.AddMinutes(30)) is null, "Responsive sessions continue beyond the trial deadline");
        check(FanControlLifetime.StopReason(time.AddSeconds(11), time, true, time, true, false, time.AddSeconds(11)) is not null, "Continuous control stops on heartbeat timeout");
        check(FanControlLifetime.StopReason(time.AddSeconds(11), time, true, time.AddSeconds(11), true, false, time) is not null, "A resume or telemetry gap ends continuous control");
        check(FanControlLifetime.StopReason(time, time, true, time, false, false, time) is not null
            && FanControlLifetime.StopReason(time, time, true, time, true, true, time) is not null, "Application exit and explicit stop end control");
    }
    private sealed class Fake(FanCurveSnapshot current, ThermalReading thermal) : IFanCurveTransport
    {
        public FanCurveSnapshot Current = current;
        public int Writes;
        public bool IgnoreCurveWrite, CorruptGpu, ThrowCurve;
        public int CurveReturn = 14;
        public List<int> ModeRequests = [];
        public FanCurveSnapshot ReadFanCurves() => Current;
        public ThermalReading ReadThermals() => thermal;
        public int SetFanCurve(FanCurve[] curves)
        {
            Writes++;
            if (ThrowCurve) throw new IOException("simulated curve failure");
            if (!IgnoreCurveWrite)
            {
                var clone = curves.Select(c => new FanCurve(c.Nodes.ToArray())).ToArray();
                if (CorruptGpu) clone[1].Nodes[2] = clone[1].Nodes[2] with { RawDuty = clone[1].Nodes[2].RawDuty - 1 };
                Current = Current with { Curves = clone };
            }
            return CurveReturn;
        }
        public int SetFanMode(int mode) { Writes++; ModeRequests.Add(mode); return 121; }
        public int SaveFanMode(int mode) { Writes++; Current = Current with { Settings = Current.Settings with { FanMode = mode } }; return 1; }
    }
}
