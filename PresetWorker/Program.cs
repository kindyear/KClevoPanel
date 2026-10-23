using System.Text.Json;
using KClevoPanel.Core;
using KClevoPanel.PresetWorker;

try
{
    if (args.SequenceEqual(new[] { "--self-test-journal" })) { JournalTests.Run(); return; }
    using var native = new NativePresets();
    if (args.SequenceEqual(new[] { "--inspect" }))
    {
        var snapshot = native.Read();
        Console.WriteLine(JsonSerializer.Serialize(new { Snapshot = snapshot,
            Plans = Enumerable.Range(0, 4).Select(m => DirectPresetPlan.Create(snapshot, m)),
            RecoveryNeeded = PendingJournal.Find(), native.Library, native.FirmwareWriteCalls, native.MirrorWriteCalls, Backend = "DirectDchu", HardwareEffectVerified = false }));
        return;
    }
    if (args.SequenceEqual(new[] { "--recover", "--apply" }))
    {
        using var gate = new Mutex(false, @"Local\KClevoPanel.DirectPresets.X17");
        if (!EnterGate(gate)) throw new IOException("另一个直接模式事务正在执行");
        try
        {
            var pending = PendingJournal.Find();
            pending?.Recover(native);
            Console.WriteLine(JsonSerializer.Serialize(new { RecoveryNeeded = PendingJournal.Find(), After = native.Read(), native.FirmwareWriteCalls, native.MirrorWriteCalls }));
        }
        finally { gate.ReleaseMutex(); }
        return;
    }
    if (args.Length != 0)
    {
        if (args.Length != 3 || args[0] != "--roundtrip" || args[1] != "entertainment" || args[2] != "--apply")
            throw new ArgumentException("仅支持 --inspect 或 --roundtrip entertainment --apply；无参数进入应用IPC");
        using var gate = new Mutex(false, @"Local\KClevoPanel.DirectPresets.X17");
        if (!EnterGate(gate)) throw new IOException("另一个直接模式事务正在执行");
        try
        {
            if (PendingJournal.Find() is { } unresolved) throw new IOException("上次事务未完成，先核对恢复日志：" + unresolved.Path);
            using var log = new PresetJournal();
            var before = native.Read();
            if (before.Mode != 2 || !DirectPresetPlan.Create(before, 3).CanApply)
                throw new InvalidOperationException("该测试只允许从已核对的性能档短暂切换娱乐，再恢复性能");
            var thermalsBefore = native.ReadThermals();
            DirectPresetResult? changed = null, restored = null;
            var observations = new List<object>();
            try
            {
                log.Record("RoundtripStarted", before);
                changed = DirectPresetTransaction.Apply(native, 3, before.Token, log.Record);
                if (!changed.Accepted) throw new IOException(changed.Status);
                for (int i = 0; i < 3; i++)
                { Thread.Sleep(1000); observations.Add(new { Settings = native.Read(), Thermal = native.ReadThermals() }); }
            }
            finally
            {
                var current = native.Read();
                restored = DirectPresetTransaction.Apply(native, before.Mode, current.Token, log.Record);
                if (!restored.Accepted || native.Read().Token != before.Token)
                    throw new IOException("性能档恢复未确认；请检查日志 " + log.Path);
            }
            log.Record("RoundtripCompleted", new { After = native.Read() });
            Console.WriteLine(JsonSerializer.Serialize(new { Passed = changed!.Accepted && restored!.Accepted,
                Before = before, ThermalsBefore = thermalsBefore, Changed = changed, Observations = observations,
                Restored = restored, After = native.Read(), native.FirmwareWriteCalls, native.MirrorWriteCalls,
                JournalPath = log.Path, native.Library, Backend = "DirectDchu", HardwareEffectVerified = false }));
        }
        finally { gate.ReleaseMutex(); }
        return;
    }
    using var transactionGate = new Mutex(false, @"Local\KClevoPanel.DirectPresets.X17");
    string? line;
    while ((line = Console.ReadLine()) is not null)
    {
        object result;
        if (line == "read")
        {
            var snapshot = native.Read();
            var plan = DirectPresetPlan.Create(snapshot, snapshot.Mode);
            string[] labels = ["安静", "省电", "性能", "娱乐"];
            var pending = PendingJournal.Find();
            result = new PresetState(snapshot.Mode is >= 0 and <= 3 ? snapshot.Mode : null, plan.CanApply && pending is null,
                pending is not null ? "上次直接事务未完成，请核对恢复日志" : plan.CanApply ? "固件档位 · " + labels[snapshot.Mode] : plan.Reason!,
                "DirectDchu", "SavedSelection", snapshot.Token, pending?.Path);
        }
        else
        {
            string[] parts = line.Split(':');
            if (parts.Length != 3 || parts[0] != "apply" || !int.TryParse(parts[1], out int mode) || mode is < 0 or > 3
                || parts[2].Length != 64 || parts[2].Any(c => !Uri.IsHexDigit(c)))
                result = new PresetState(null, false, "请求不在直接模式白名单中", "DirectDchu");
            else
            {
                using var fanLease = new Mutex(false, @"Local\KClevoPanel.FanTrial.X17");
                if (!EnterGate(fanLease)) throw new IOException("曲线试用期间不能切换性能档位");
                fanLease.ReleaseMutex();
                if (!EnterGate(transactionGate)) throw new IOException("另一个直接模式事务正在执行");
                DirectPresetResult changed;
                string? journalPath = null;
                try
                {
                    if (PendingJournal.Find() is { } pending) throw new IOException("先核对未完成事务：" + pending.Path);
                    using var journal = new PresetJournal(); journalPath = journal.Path;
                    _ = native.ReadThermals(); // Require a valid target telemetry response before the first write.
                    changed = DirectPresetTransaction.Apply(native, mode, parts[2], journal.Record);
                }
                finally { transactionGate.ReleaseMutex(); }
                result = new PresetState(changed.After?.Mode, changed.Accepted, changed.Status,
                    "DirectDchu", changed.Verification, changed.After?.Token, journalPath);
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(result));
    }
}

catch (Exception error)
{
    Console.WriteLine(JsonSerializer.Serialize(new PresetState(null, false, "直接档位通道未就绪：" + error.Message, "DirectDchu")));
    Environment.ExitCode = 1;
}

static bool EnterGate(Mutex gate)
{ try { return gate.WaitOne(2000); } catch (AbandonedMutexException) { return true; } }

internal sealed class PresetJournal : IDisposable
{
    public string Path { get; }
    private readonly FileStream _stream;
    private readonly StreamWriter _writer;
    public PresetJournal()
    {
        string directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KClevoPanel", "Presets");
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N") + ".jsonl");
        _stream = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(_stream, System.Text.Encoding.UTF8, leaveOpen: true);
    }
    public void Record(string stage, object data)
    { _writer.WriteLine(JsonSerializer.Serialize(new { Time = DateTimeOffset.Now, Stage = stage, Data = data })); _writer.Flush(); _stream.Flush(true); }
    public void Dispose() { _writer.Dispose(); _stream.Dispose(); }
}
