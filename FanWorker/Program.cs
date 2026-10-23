using System.Diagnostics;
using System.Text.Json;
using KClevoPanel.Core;
using KClevoPanel.PresetWorker;

string? session = args.Length == 2 ? Path.GetFullPath(args[1]) : null;
NativePresets? transaction = null;
try
{
    if (args.Length == 1 && args[0] == "--remove-startup")
    {
        // MSI maintenance only: no DLL loading or hardware access. Do not touch a portable copy's entry.
        string executable = Path.Combine(AppContext.BaseDirectory, "KClevoPanel.exe");
        if (StartupRegistration.IsEnabled(executable)) StartupRegistration.SetEnabled(executable, false);
        return;
    }
    if (args.Length == 1 && args[0] is "--inspect" or "--serve")
    {
        using var native = new NativePresets();
        FanInspection Inspect()
        {
            var thermal = native.ReadThermals();
            return new(FanCurveSnapshot.Decode(thermal, native.Read()), thermal, native.Library, native.FirmwareWriteCalls, native.MirrorWriteCalls);
        }
        if (args[0] == "--inspect") Console.WriteLine(JsonSerializer.Serialize(Inspect()));
        else while (Console.ReadLine() is { } input)
        {
            if (input == "exit") break;
            if (input != "read") throw new IOException("非白名单只读请求");
            Console.WriteLine(JsonSerializer.Serialize(Inspect()));
        }
        return;
    }
    if (session is null || args[0] is not ("--trial" or "--guard" or "--mode" or "--reconcile")) throw new ArgumentException("不支持的风扇请求");
    if (args[0] == "--guard") { Guard(); return; }
    transaction = new NativePresets(); RequireLibrary(transaction);
    if (args[0] == "--reconcile")
    {
        using var lease = new Mutex(false, @"Local\KClevoPanel.FanTrial.X17"); Enter(lease);
        try
        {
            var baseline = Read<FanCurveSnapshot>("baseline.json"); var now = transaction.ReadFanCurves();
            bool same = FanCurveProtocol.Matches(now, baseline.Curves) && now.Settings.Token == baseline.Settings.Token;
            var result = new FanTrialStatus(same ? "Restored" : "RecoveryRequired", same ? "只读复核：原节点和模式记录一致，无需恢复写入" : "当前状态与基线不同，需要显式恢复",
                CurveRestored: same, ModeRestored: same, Verification: "ReadOnlyBaselineComparison");
            if (same) Write("status.json", result);
            Console.WriteLine(JsonSerializer.Serialize(result));
        }
        finally { lease.ReleaseMutex(); }
        return;
    }
    if (args[0] == "--mode")
    {
        var request = Read<FanModeRequest>("request.json");
        if (request.Mode is not (0 or 1)) throw new IOException("仅允许自动和最大模式");
        using var lease = new Mutex(false, @"Local\KClevoPanel.FanTrial.X17"); Enter(lease);
        try
        {
            using var gate = new Mutex(false, @"Local\KClevoPanel.DirectPresets.X17"); Enter(gate);
            try
            {
                if (UnresolvedTrials(session).Any() || PendingJournal.Find() is not null) throw new IOException("请先恢复未完成的风扇或性能事务");
                if (PendingModes().Any() && request.Mode != 0) throw new IOException("上次风扇模式未确认，请先切换自动模式恢复");
                string journal = Path.Combine(ModeDirectory(), DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N") + ".jsonl");
                var result = FanModeTransaction.Apply(transaction, request.Mode, request.ExpectedToken, (stage, data) => Append(journal, stage, data));
                if (result.Confirmed && request.Mode == 0) foreach (var old in PendingModes()) Append(old, "RecoveredByAuto", new { Current = result.After });
                Console.WriteLine(JsonSerializer.Serialize(result));
            }
            finally { gate.ReleaseMutex(); }
        }
        finally { lease.ReleaseMutex(); }
        return;
    }
    var trial = Read<FanTrialRequest>("request.json");
    if (trial.TargetMode is not (1 or 6) || trial.TargetMode == 1 && trial.Continuous) throw new IOException("试用目标不在白名单");
    if (PendingJournal.Find() is not null || PendingModes().Any() || UnresolvedTrials(session).Any()) throw new IOException("存在未完成事务，请先恢复");
    var before = transaction.ReadFanCurves(); FanCurveProtocol.Validate(before, trial.Curves);
    if (!trial.Continuous && trial.TargetMode == 6 && FanCurveProtocol.Matches(before, trial.Curves)) throw new IOException("试用曲线没有变化");
    if (before.Token != trial.ExpectedToken) throw new IOException("当前曲线或设置已变化，请重新读取");
    FanCurveTransaction.RequireCool(transaction.ReadThermals(), true); Write("baseline.json", before);
    using var guardian = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true, ArgumentList = { "--guard", session } }) ?? throw new IOException("无法启动恢复进程");
    for (int i = 0; i < 300 && !File.Exists(FileAt("ready")); i++)
    { if (guardian.HasExited) throw new IOException("恢复进程未就绪"); Thread.Sleep(100); }
    if (!File.Exists(FileAt("ready"))) throw new IOException("恢复进程初始化超时");
    using var transactionGate = new Mutex(false, @"Local\KClevoPanel.DirectPresets.X17"); Enter(transactionGate);
    try
    {
        if (transaction.ReadFanCurves().Token != before.Token || PendingJournal.Find() is not null || PendingModes().Any()) throw new IOException("写入前状态已变化");
        FanCurveTransaction.RequireCool(transaction.ReadThermals(), true);
        if (File.Exists(FileAt("stop")) || !AppAlive(trial) || guardian.HasExited) throw new IOException("请求已取消或恢复进程退出");
        var started = DateTimeOffset.UtcNow; Write("armed.json", new Arm(started, started.AddSeconds(30)));
        Write("status.json", new FanTrialStatus("Preparing", "正在应用并核对固件状态"));
        if (trial.TargetMode == 6) _ = FanCurveTransaction.Apply(transaction, before, trial.Curves, Thread.Sleep, trial.Continuous);
        else
        {
            var changed = FanModeTransaction.Apply(transaction, 1, before.Token, (stage, data) => Write("mode-" + stage + ".json", data));
            if (!changed.Confirmed) throw new IOException(changed.Message);
        }
        File.WriteAllText(FileAt("active"), "1");
        var active = Status(transaction, trial.Continuous ? null : started.AddSeconds(30)); Write("status.json", active);
        Console.WriteLine(JsonSerializer.Serialize(active));
    }
    finally { transactionGate.ReleaseMutex(); }
}
catch (Exception error)
{
    var failure = new FanTrialStatus("Failed", error.Message, CurveReturn: transaction?.LastCurveReturn,
        ModeReturn: transaction?.LastFanModeReturn, MirrorReturn: transaction?.LastFanMirrorReturn);
    if (session is not null && Directory.Exists(session))
    {
        Write("last-error.json", failure);
        if (File.Exists(FileAt("armed.json"))) File.WriteAllText(FileAt("stop"), "1");
        else Write("status.json", failure);
    }
    Console.WriteLine(JsonSerializer.Serialize(failure)); Environment.ExitCode = 1;
}
finally { transaction?.Dispose(); }

void Guard()
{
    using var lease = new Mutex(false, @"Local\KClevoPanel.FanTrial.X17");
    if (!TryEnter(lease, 0)) throw new IOException("另一个风扇会话正在运行");
    try
    {
        var baseline = Read<FanCurveSnapshot>("baseline.json"); var request = Read<FanTrialRequest>("request.json");
        FanCurveProtocol.Validate(baseline, request.Curves);
        if (request.TargetMode is not (1 or 6)) throw new IOException("非法目标模式");
        using var native = new NativePresets(); RequireLibrary(native); _ = FanCurveProtocol.Frame(baseline.Curves);
        using var self = Process.GetCurrentProcess(); Write("guardian.json", new { Pid = self.Id, StartTicks = self.StartTime.ToUniversalTime().Ticks });
        File.WriteAllText(FileAt("ready"), "1");
        for (int i = 0; i < 300 && !File.Exists(FileAt("armed.json")); i++)
        { if (File.Exists(FileAt("stop")) || !AppAlive(request)) return; Thread.Sleep(100); }
        if (!File.Exists(FileAt("armed.json"))) return;
        var arm = Read<Arm>("armed.json"); var started = arm.Started == default ? arm.Deadline.AddSeconds(-30) : arm.Started;
        var lastSample = DateTimeOffset.UtcNow; string reason = "已结束控制"; bool confirmed = false;
        ThermalReading? lastThermal = null; int evidenceCount = 0;
        try
        {
            while (true)
            {
                bool continuous = request.Continuous || request.TargetMode == 6 && File.Exists(FileAt("keep"));
                DateTimeOffset? heartbeat = File.Exists(FileAt("heartbeat")) ? File.GetLastWriteTimeUtc(FileAt("heartbeat")) : null;
                reason = FanControlLifetime.StopReason(DateTimeOffset.UtcNow, started, continuous, heartbeat, AppAlive(request), File.Exists(FileAt("stop")), lastSample) ?? "";
                if (reason.Length != 0) break;
                using var sampleGate = new Mutex(false, @"Local\KClevoPanel.DirectPresets.X17"); Enter(sampleGate);
                try
                {
                    lastThermal = native.ReadThermals(); FanCurveTransaction.RequireCool(lastThermal, false);
                    if (File.Exists(FileAt("active")))
                    {
                        var current = native.ReadFanCurves();
                        if (current.Settings.FanMode != request.TargetMode || current.Settings.Mode != baseline.Settings.Mode || current.Settings.Dtt != baseline.Settings.Dtt
                            || current.Settings.FanOffset != baseline.Settings.FanOffset || current.Settings.TurboFan != baseline.Settings.TurboFan
                            || request.TargetMode == 6 && !FanCurveProtocol.Matches(current, request.Curves)) throw new IOException("风扇或性能设置被其他程序更改");
                        if (evidenceCount == 0 || evidenceCount < 3 && DateTimeOffset.UtcNow - started > TimeSpan.FromSeconds(evidenceCount * 6))
                        {
                            Write("active-evidence-" + evidenceCount + ".json", new { Current = current, Thermal = lastThermal, Continuous = continuous, At = DateTimeOffset.UtcNow });
                            evidenceCount++;
                        }
                        confirmed = true;
                        var active = Status(native, continuous ? null : started.AddSeconds(30));
                        var previous = ReadOptional<FanTrialStatus>("status.json");
                        Write("status.json", active with { CurveReturn = previous?.CurveReturn, ModeReturn = previous?.ModeReturn, MirrorReturn = previous?.MirrorReturn });
                    }
                    lastSample = DateTimeOffset.UtcNow;
                }
                finally { sampleGate.ReleaseMutex(); }
                Thread.Sleep(750);
            }
        }
        catch (Exception error) { reason = "保护停止：" + error.Message; Write("protection-evidence.json", new { Error = error.Message, Thermal = lastThermal }); }
        Write("status.json", new FanTrialStatus("Restoring", "正在恢复进入前的风扇设置"));
        using var gate = new Mutex(false, @"Local\KClevoPanel.DirectPresets.X17"); bool locked = false;
        try
        {
            Enter(gate); locked = true;
            var restored = FanCurveTransaction.Restore(native, baseline, Thread.Sleep);
            var result = new FanTrialStatus(restored.CurveRestored && restored.ModeRestored ? "Restored" : "RecoveryFailed",
                restored.CurveRestored && restored.ModeRestored ? reason + "，原节点与模式记录已核对" + (confirmed ? "" : "（应用未确认）") : "恢复未确认：" + restored.Error,
                CurveRestored: restored.CurveRestored, ModeRestored: restored.ModeRestored, CurveReturn: restored.CurveReturn,
                ModeReturn: restored.ModeReturn, MirrorReturn: restored.MirrorReturn, Verification: "RuntimeNodesAndSavedMode");
            Write("status.json", result);
            try { Write("restoration-evidence.json", new { Result = result, After = native.ReadFanCurves(), Thermal = native.ReadThermals(), native.FirmwareWriteCalls, native.MirrorWriteCalls }); }
            catch (Exception evidenceError) { Write("evidence-error.json", new { Error = evidenceError.Message }); }
        }
        catch (Exception error) { Write("status.json", new FanTrialStatus("RecoveryFailed", "恢复未确认：" + error.Message)); }
        finally { if (locked) gate.ReleaseMutex(); }
    }
    finally { lease.ReleaseMutex(); }
}
FanTrialStatus Status(NativePresets native, DateTimeOffset? deadline) => new("Active", deadline.HasValue ? "试用中，30 秒后恢复" : "自定义曲线运行中，退出或心跳中断时恢复",
    deadline, CurveReturn: native.LastCurveReturn, ModeReturn: native.LastFanModeReturn, MirrorReturn: native.LastFanMirrorReturn, Verification: "RuntimeNodesAndSavedMode");
static bool AppAlive(FanTrialRequest r) { try { using var app = Process.GetProcessById(r.AppPid); return !app.HasExited && app.StartTime.ToUniversalTime().Ticks == r.AppStartTicks; } catch { return false; } }
static void RequireLibrary(NativePresets n) { if (n.Library.Sha256 != TargetDchuLibrary.DriverStoreHash) throw new IOException("风扇写入仅允许已核对的DriverStore库，不尝试其他接口"); }
string FileAt(string name) => Path.Combine(session!, name);
T Read<T>(string name) => JsonSerializer.Deserialize<T>(File.ReadAllText(FileAt(name))) ?? throw new IOException("风扇记录无效");
T? ReadOptional<T>(string name) where T : class { try { return Read<T>(name); } catch { return null; } }
void Write<T>(string name, T data)
{
    string path = FileAt(name), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
    using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { JsonSerializer.Serialize(stream, data); stream.Flush(true); }
    File.Move(temp, path, true);
}
static string ModeDirectory() { string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KClevoPanel", "FanModes"); Directory.CreateDirectory(path); return path; }
static IEnumerable<string> PendingModes()
{
    foreach (var path in Directory.EnumerateFiles(ModeDirectory(), "*.jsonl"))
    {
        string? stage = null;
        try { if (new FileInfo(path).Length < 1024 * 1024) { using var last = JsonDocument.Parse(File.ReadLines(path).Last(l => !string.IsNullOrWhiteSpace(l))); stage = last.RootElement.GetProperty("Stage").GetString(); } } catch { }
        if (stage is not ("Completed" or "RolledBack" or "RecoveredByAuto")) yield return path;
    }
}
static IEnumerable<string> UnresolvedTrials(string? except)
{
    string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KClevoPanel", "FanTrials");
    if (!Directory.Exists(root)) yield break;
    foreach (var directory in Directory.EnumerateDirectories(root))
    {
        if (directory == except || !File.Exists(Path.Combine(directory, "armed.json"))) continue;
        FanTrialStatus? status = null; try { status = JsonSerializer.Deserialize<FanTrialStatus>(File.ReadAllText(Path.Combine(directory, "status.json"))); } catch { }
        if (status?.State != "Restored") yield return directory;
    }
}
static void Append(string path, string stage, object data)
{
    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
    using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
    writer.WriteLine(JsonSerializer.Serialize(new { At = DateTimeOffset.UtcNow, Stage = stage, Data = data })); writer.Flush(); stream.Flush(true);
}
static bool TryEnter(Mutex gate, int ms) { try { return gate.WaitOne(ms); } catch (AbandonedMutexException) { return true; } }
static void Enter(Mutex gate) { if (!TryEnter(gate, 3000)) throw new IOException("另一项风扇或性能操作正在执行"); }
internal sealed record Arm(DateTimeOffset Started, DateTimeOffset Deadline);
