using System.Diagnostics;
using System.Text.Json;

namespace KClevoPanel.Core;

public sealed class DirectPresetClient : IDisposable
{
    private readonly SemaphoreSlim _serial = new(1);
    private Process? _process;
    private bool _disposed;
    private string? _token;
    private DateTimeOffset _retryAfter;
    public Task<PresetState> ReadAsync() => ExchangeAsync(null);
    public Task<PresetState> ChangeAsync(int mode) => mode is >= 0 and <= 3 ? ExchangeAsync(mode)
        : Task.FromResult(new PresetState(null, false, "无效档位", "DirectDchu"));
    private async Task<PresetState> ExchangeAsync(int? mode)
    {
        await _serial.WaitAsync();
        try
        {
            if (_disposed) return new(null, false, "直接档位通道已关闭", "DirectDchu");
            if (mode.HasValue && _token is null) return new(null, false, "请先读取当前档位", "DirectDchu");
            if (DateTimeOffset.Now < _retryAfter) return new(null, false, "直接通道暂不可用，稍后重试", "DirectDchu");
            bool first = _process is null;
            if (first)
            {
                string path = Path.Combine(AppContext.BaseDirectory, "KClevoPanel.PresetWorker.exe");
                if (!File.Exists(path)) return new(null, false, "缺少直接档位组件", "DirectDchu");
                _process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true });
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(first ? 30 : mode.HasValue ? 20 : 4));
            string request = mode.HasValue ? $"apply:{mode}:{_token}" : "read";
            await _process!.StandardInput.WriteLineAsync(request);
            await _process.StandardInput.FlushAsync(timeout.Token);
            string? line = await _process.StandardOutput.ReadLineAsync(timeout.Token);
            var state = line is null ? throw new IOException("直接档位进程已结束")
                : JsonSerializer.Deserialize<PresetState>(line) ?? throw new IOException("档位状态无效");
            _token = state.Available ? state.StateToken : null;
            return state;
        }
        catch (Exception error)
        {
            Stop(); _token = null; _retryAfter = DateTimeOffset.Now.AddSeconds(10);
            return new(null, false, mode.HasValue ? "直接切换结果未确认；不会重试。请检查 Presets 恢复日志"
                : "直接档位暂不可用：" + error.GetType().Name, "DirectDchu");
        }
        finally { _serial.Release(); }
    }
    private void Stop()
    {
        var process = _process; _process = null;
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
        process.Dispose();
    }
    public void Dispose() { _disposed = true; Stop(); }
}
