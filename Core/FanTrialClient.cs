using System.Diagnostics;
using System.Text.Json;

namespace KClevoPanel.Core;

public sealed class FanTrialClient : IDisposable
{
    private readonly SemaphoreSlim _readSerial = new(1);
    private Process? _reader;
    private bool _disposed, _ownsSession;
    private DateTimeOffset _retryAfter;
    public string? Session { get; private set; }
    public bool Busy { get; private set; }
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KClevoPanel", "FanTrials");
    public FanTrialClient(bool ignoreSavedSessions = false)
    {
        if (ignoreSavedSessions || !Directory.Exists(Root)) return;
        Session = PendingSessions().FirstOrDefault();
    }
    public FanTrialStatus? Status => Session is null ? null : ReadStatus(Session);
    public bool NeedsRecovery => Session is not null && File.Exists(Path.Combine(Session, "armed.json")) && Status?.State != "Restored";
    public bool OwnsSession => _ownsSession;
    public async Task<FanCurveSnapshot> InspectAsync() => (await InspectStateAsync()).Snapshot;
    public async Task<FanInspection> InspectStateAsync()
    {
        await _readSerial.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (DateTimeOffset.UtcNow < _retryAfter) throw new IOException("只读风扇通道稍后可重新连接");
            bool first = _reader is null;
            if (first) _reader = Process.Start(StartInfo("--serve")) ?? throw new IOException("无法启动风扇读取进程");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(first ? 35 : 4));
            await _reader!.StandardInput.WriteLineAsync("read"); await _reader.StandardInput.FlushAsync(timeout.Token);
            var line = await _reader.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("风扇读取进程已结束");
            using var json = JsonDocument.Parse(line);
            if (!json.RootElement.TryGetProperty("Snapshot", out _)) throw new IOException(json.RootElement.GetProperty("Message").GetString());
            return JsonSerializer.Deserialize<FanInspection>(line) ?? throw new IOException("风扇读取结果无效");
        }
        catch { StopReader(); _retryAfter = DateTimeOffset.UtcNow.AddSeconds(10); throw; }
        finally { _readSerial.Release(); }
    }
    public async Task<FanTrialStatus> ReconcileAsync()
    {
        FanTrialStatus result = new("Restored", "没有待恢复记录");
        foreach (var pending in PendingSessions())
        {
            if (Session != pending) _ownsSession = false;
            Session = pending;
            if (GuardianAlive()) return Status ?? new("Preparing", "恢复进程正在运行");
            result = JsonSerializer.Deserialize<FanTrialStatus>(await RunAsync("--reconcile", Session)) ?? throw new IOException("复核结果无效");
            if (result.State != "Restored") return result;
        }
        return result;
    }
    public async Task<FanTrialStatus> StartAsync(FanCurveSnapshot baseline, FanCurve[] proposed, bool continuous = false)
    {
        if (_disposed || Busy || NeedsRecovery) throw new IOException("请先结束或恢复上次风扇会话");
        FanCurveProtocol.Validate(baseline, proposed);
        if (!continuous && FanCurveProtocol.Matches(baseline, proposed)) throw new IOException("请先修改中间节点");
        Busy = true; _ownsSession = true;
        try
        {
            Session = NewSession(Root);
            using var app = Process.GetCurrentProcess();
            var request = new FanTrialRequest(proposed, baseline.Token, app.Id, app.StartTime.ToUniversalTime().Ticks, continuous);
            await File.WriteAllTextAsync(Path.Combine(Session, "request.json"), JsonSerializer.Serialize(request)); Heartbeat();
            return JsonSerializer.Deserialize<FanTrialStatus>(await RunAsync("--trial", Session)) ?? throw new IOException("风扇结果无效");
        }
        finally { Busy = false; }
    }
    public async Task<FanModeResult> SetModeAsync(FanCurveSnapshot baseline, int mode)
    {
        if (_disposed || Busy || NeedsRecovery || mode is not (0 or 1)) throw new IOException("当前不能切换风扇模式");
        Busy = true;
        try
        {
            string directory = NewSession(Path.Combine(Path.GetDirectoryName(Root)!, "FanModeRequests"));
            await File.WriteAllTextAsync(Path.Combine(directory, "request.json"), JsonSerializer.Serialize(new FanModeRequest(mode, baseline.Token)));
            return JsonSerializer.Deserialize<FanModeResult>(await RunAsync("--mode", directory)) ?? throw new IOException("模式结果无效");
        }
        finally { Busy = false; }
    }
    public void Heartbeat()
    {
        if (!_ownsSession || Session is null || _disposed || !Busy && !NeedsRecovery) return;
        try { File.WriteAllText(Path.Combine(Session, "heartbeat"), DateTimeOffset.UtcNow.ToString("O")); } catch { }
    }
    public void Keep()
    {
        if (!_ownsSession || Session is null || Status?.State != "Active" || Status.Deadline is null) throw new IOException("当前没有可保持的曲线试用");
        Heartbeat(); File.WriteAllText(Path.Combine(Session, "keep"), "1");
    }
    public void RequestStop()
    {
        if (Session is null || Status?.State == "Restored") return;
        File.WriteAllText(Path.Combine(Session, "stop"), "1");
        if (!File.Exists(Path.Combine(Session, "armed.json"))) return;
        if (!GuardianAlive()) { using var recovery = Process.Start(StartInfo("--guard", Session)); }
    }
    public async Task StopAndWaitAsync()
    {
        if (!NeedsRecovery && !Busy) return;
        RequestStop();
        for (int i = 0; i < 110; i++)
        {
            var state = Status;
            if (state?.State == "Restored" || !NeedsRecovery && state?.State == "Failed")
            {
                string? other = PendingSessions().FirstOrDefault();
                if (other is null) return;
                Session = other; _ownsSession = false;
                throw new IOException("仍有其他未恢复会话，请先核对并显式恢复：" + other);
            }
            if (state?.State == "RecoveryFailed") throw new IOException(state.Message);
            await Task.Delay(400);
        }
        throw new IOException("恢复仍未确认，不再下发新的控制请求");
    }
    private bool GuardianAlive()
    {
        try
        {
            var identity = JsonSerializer.Deserialize<GuardianIdentity>(File.ReadAllText(Path.Combine(Session!, "guardian.json")))!;
            using var process = Process.GetProcessById(identity.Pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == identity.StartTicks;
        }
        catch { return false; }
    }
    private static string NewSession(string root)
    { string path = Path.Combine(root, DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static FanTrialStatus? ReadStatus(string directory)
    { try { return JsonSerializer.Deserialize<FanTrialStatus>(File.ReadAllText(Path.Combine(directory, "status.json"))); } catch { return null; } }
    private static IEnumerable<string> PendingSessions() => !Directory.Exists(Root) ? [] : Directory.EnumerateDirectories(Root).OrderDescending()
        .Where(p => File.Exists(Path.Combine(p, "armed.json")) && ReadStatus(p)?.State != "Restored");
    private static ProcessStartInfo StartInfo(params string[] args)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "KClevoPanel.FanWorker.exe");
        if (!File.Exists(path)) throw new IOException("缺少风扇组件");
        var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) info.ArgumentList.Add(arg); return info;
    }
    private static async Task<string> RunAsync(params string[] args)
    {
        using var process = Process.Start(StartInfo(args)) ?? throw new IOException("风扇进程未启动");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        return await process.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("风扇进程没有返回结果");
    }
    private void StopReader()
    { if (_reader is null) return; try { if (!_reader.HasExited) _reader.Kill(); } catch { } _reader.Dispose(); _reader = null; }
    public void Dispose() { _disposed = true; StopReader(); }
    private sealed record GuardianIdentity(int Pid, long StartTicks);
}
