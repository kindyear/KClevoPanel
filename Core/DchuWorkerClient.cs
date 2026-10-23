using System.Diagnostics;
using System.Text.Json;

namespace KClevoPanel.Core;

// One serialized query worker. Failure is latched; no automatic retry storm.
public sealed class DchuWorkerClient : IDisposable
{
    private Process? _process;
    private bool _faulted;
    public string State { get; private set; } = "等待读取";
    public async Task<ThermalReading?> ReadAsync()
    {
        if (_faulted) return null;
        bool first = _process is null;
        try
        {
            if (first)
            {
                string path = Path.Combine(AppContext.BaseDirectory, "KClevoPanel.TelemetryWorker.exe");
                if (!File.Exists(path)) throw new FileNotFoundException("缺少只读采样进程");
                _process = new Process { StartInfo = new ProcessStartInfo(path)
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
                _process.Start();
            }
            using var timeout = new CancellationTokenSource(first ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(2));
            await _process!.StandardInput.WriteLineAsync("read");
            await _process.StandardInput.FlushAsync(timeout.Token);
            string? line = await _process.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is null) throw new IOException("只读采样进程已结束");
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.TryGetProperty("Error", out var error)) throw new IOException(error.GetString());
            var reading = JsonSerializer.Deserialize<ThermalReading>(line) ?? throw new IOException("只读采样返回无效");
            if (!reading.BoundsIntact) throw new IOException("采样缓冲区检查失败");
            State = reading.State;
            return reading;
        }
        catch (Exception e)
        {
            State = e is OperationCanceledException ? "只读通道超时，已停止" : "只读通道不可用：" + e.Message;
            _faulted = true; Stop(); return null;
        }
    }
    private void Stop()
    {
        if (_process is null) return;
        try { if (!_process.HasExited) _process.Kill(); } catch (InvalidOperationException) { }
        _process.Dispose(); _process = null;
    }
    public void Dispose() { _faulted = true; Stop(); }
}
