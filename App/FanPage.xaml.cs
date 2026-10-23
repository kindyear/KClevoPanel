using System.Text.Json;
using KClevoPanel.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace KClevoPanel.App;

public sealed partial class FanPage : UserControl, IDisposable
{
    private static bool IsSmoke => Environment.GetCommandLineArgs().Contains("--smoke-test");
    private readonly FanTrialClient _client = new(ignoreSavedSessions: IsSmoke);
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private FanCurveSnapshot? _baseline, _current;
    private bool _busy, _reading, _loaded, _lastActive, _startedThisRun, _polling, _disposed, _showSession;
    private string? _lastStatus;
    private DateTimeOffset? _thermalTime;
    private DateTimeOffset _lastInspect;
    private ThermalReading? _latest;
    internal long WriteRequests { get; private set; }
    public bool TrialInProgress => _busy || _client.NeedsRecovery;
    public event Action? ActivityChanged;
    private static string DraftPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KClevoPanel", "fan-curve-draft.json");
    public FanPage()
    {
        InitializeComponent(); UpdateColors();
        ActualThemeChanged += (_, _) => UpdateColors();
        Loaded += (_, _) => { UpdateColors(); _poll.Start(); };
        _poll.Tick += async (_, _) => await PollAsync();
        CpuEditor.Edited += DraftEdited; GpuEditor.Edited += DraftEdited;
        _showSession = _client.NeedsRecovery;
    }
    private void UpdateColors()
    {
        bool light = ActualTheme == ElementTheme.Light;
        CpuEditor.Configure("CPU 风扇", new SolidColorBrush(light ? Windows.UI.Color.FromArgb(255, 0, 103, 192) : Windows.UI.Color.FromArgb(255, 96, 205, 255)));
        GpuEditor.Configure("GPU 风扇", new SolidColorBrush(light ? Windows.UI.Color.FromArgb(255, 0, 130, 114) : Windows.UI.Color.FromArgb(255, 107, 216, 194)));
    }
    public async Task EnsureLoadedAsync() { if (!_loaded) await ReadAsync(); }
    private async void ReadClicked(object sender, RoutedEventArgs e) => await ReadAsync();
    private async Task ReadAsync()
    {
        if (_reading || _busy) return;
        _reading = true; UpdateEnabled();
        try
        {
            if (_client.NeedsRecovery) { _showSession = true; _ = await _client.ReconcileAsync(); }
            var state = await _client.InspectStateAsync(); bool first = !_loaded;
            _baseline = _current = state.Snapshot; _loaded = true; _lastInspect = DateTimeOffset.UtcNow;
            CpuEditor.LoadCurve(_baseline.Curves[0]); GpuEditor.LoadCurve(_baseline.Curves[1]);
            DraftLabel.Text = "已载入固件曲线";
            if (first && !IsSmoke && File.Exists(DraftPath))
            {
                try
                {
                    var draft = JsonSerializer.Deserialize<FanCurve[]>(await File.ReadAllTextAsync(DraftPath))!;
                    FanCurveProtocol.Validate(_baseline, draft); CpuEditor.LoadCurve(draft[0]); GpuEditor.LoadCurve(draft[1]); DraftLabel.Text = "已载入本机草稿 · 尚未应用";
                }
                catch { DraftLabel.Text = "旧草稿不适用，已载入固件曲线"; }
            }
            UpdateTelemetry(state.Thermal); PaintMode();
        }
        catch (Exception error) { ReadLabel.Text = "读取未完成：" + error.Message; ShowResult("风扇通道暂不可用", error.Message, InfoBarSeverity.Warning); }
        finally { _reading = false; UpdateEnabled(); }
    }
    public void UpdateTelemetry(ThermalReading? thermal)
    {
        _latest = thermal;
        if (thermal is null || thermal.CapturedAt == _thermalTime) return;
        _thermalTime = thermal.CapturedAt;
        try
        {
            var runtime = _current is null ? null : FanCurveSnapshot.Decode(thermal, _current.Settings).Curves;
            CpuEditor.SetRuntime(runtime?[0], thermal.Fans.FirstOrDefault(f => f.Name == "CPU"), thermal.CpuTemperatureC);
            GpuEditor.SetRuntime(runtime?[1], thermal.Fans.FirstOrDefault(f => f.Name == "GPU"), thermal.GpuTemperatureC);
        }
        catch { CpuEditor.SetRuntime(null, null, null); GpuEditor.SetRuntime(null, null, null); }
    }
    private bool Changed => _current is not null && CpuEditor.Curve is { } cpu && GpuEditor.Curve is { } gpu && (!cpu.Same(_current.Curves[0]) || !gpu.Same(_current.Curves[1]));
    private void DraftEdited()
    {
        DraftLabel.Text = "曲线已修改 · 尚未应用"; UpdateEnabled();
        if (IsSmoke) return;
        try
        {
            var curves = Proposed(); Directory.CreateDirectory(Path.GetDirectoryName(DraftPath)!);
            string temp = DraftPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(curves)); File.Move(temp, DraftPath, true); DraftLabel.Text = "草稿已保存 · 尚未应用";
        }
        catch { DraftLabel.Text = "曲线已修改 · 草稿暂未保存"; }
    }
    private void ResetClicked(object sender, RoutedEventArgs e)
    { if (_current is null || _busy) return; CpuEditor.LoadCurve(_current.Curves[0]); GpuEditor.LoadCurve(_current.Curves[1]); DraftEdited(); }
    private FanCurve[] Proposed()
    {
        var curves = new[] { CpuEditor.Curve!, GpuEditor.Curve! };
        FanCurveProtocol.Validate(_baseline ?? throw new IOException("请先读取曲线"), curves); return curves;
    }
    private async void TryClicked(object sender, RoutedEventArgs e) => await ApplyAsync(false);
    private async void ApplyClicked(object sender, RoutedEventArgs e) => await ApplyAsync(true);
    private async Task ApplyAsync(bool continuous)
    {
        if (IsSmoke || _busy || _reading || _baseline is null) { PaintMode(); return; }
        FanCurve[] proposed;
        try { proposed = Proposed(); } catch (Exception e) { ShowResult("曲线无效", e.Message, InfoBarSeverity.Warning); return; }
        _busy = true; _startedThisRun = true; _showSession = true; _lastStatus = null; WriteRequests++; Signal();
        ShowResult("准备应用", "正在部署恢复进程并核对当前状态…", InfoBarSeverity.Informational);
        try
        {
            await _client.StopAndWaitAsync();
            var state = await _client.InspectStateAsync(); _baseline = _current = state.Snapshot;
            FanCurveProtocol.Validate(_baseline, proposed);
            var status = await _client.StartAsync(_baseline, proposed, continuous);
            if (status.State != "Active") ShowResult("应用未确认", status.Message, InfoBarSeverity.Warning);
            else { _current = (await _client.InspectStateAsync()).Snapshot; PaintMode(); }
        }
        catch (Exception error) { ShowResult("应用未确认", error.Message, InfoBarSeverity.Warning); }
        finally { _busy = false; PaintSession(); Signal(); }
    }
    private async void ModeClicked(object sender, RoutedEventArgs e)
    {
        if (IsSmoke || _busy || _reading || sender is not FrameworkElement element || !int.TryParse(element.Tag?.ToString(), out int mode)) { PaintMode(); return; }
        _busy = true; _showSession = false; WriteRequests++; Signal();
        try
        {
            await _client.StopAndWaitAsync();
            var before = (await _client.InspectStateAsync()).Snapshot;
            var result = await _client.SetModeAsync(before, mode);
            if (!result.Confirmed || result.After is null) throw new IOException(result.Message);
            _baseline = _current = result.After;
            ShowResult(mode == 0 ? "已切换自动" : "已切换最大风量", result.Message + "；实际转速见上方读数。", InfoBarSeverity.Success);
        }
        catch (Exception error) { ShowResult("切换未确认", error.Message, InfoBarSeverity.Warning); }
        finally { _busy = false; PaintMode(); Signal(); }
    }
    private void KeepClicked(object sender, RoutedEventArgs e)
    { if (IsSmoke) return; try { _client.Keep(); WriteRequests++; ShowResult("保持当前曲线", "将保持至退出或恢复；不会在开机时自动重放。", InfoBarSeverity.Success); } catch (Exception error) { ShowResult("保持失败", error.Message, InfoBarSeverity.Warning); } }
    private async void StopClicked(object sender, RoutedEventArgs e)
    {
        if (IsSmoke || _busy) return;
        _busy = true; _showSession = true; Signal();
        try { await _client.StopAndWaitAsync(); _current = (await _client.InspectStateAsync()).Snapshot; }
        catch (Exception error) { ShowResult("恢复未确认", error.Message, InfoBarSeverity.Error); }
        finally { _busy = false; PaintSession(); PaintMode(); Signal(); }
    }
    public void StopForExit() { try { if (!IsSmoke && _startedThisRun && _client.OwnsSession && TrialInProgress) _client.RequestStop(); } catch { } }
    public void StopForPowerChange() { try { if (!IsSmoke && _client.OwnsSession && TrialInProgress) _client.RequestStop(); } catch { } }
    private async Task PollAsync()
    {
        if (_disposed) return;
        _client.Heartbeat(); PaintSession(); Signal();
        if (!_loaded || _busy || _reading || _polling || DateTimeOffset.UtcNow - _lastInspect < TimeSpan.FromSeconds(3)) return;
        _polling = true; _lastInspect = DateTimeOffset.UtcNow;
        try
        {
            var state = await _client.InspectStateAsync();
            if (_disposed) return;
            _current = state.Snapshot; UpdateTelemetry(state.Thermal); PaintMode();
        }
        catch (Exception error) { if (!_disposed) { ModeLabel.Text = "状态暂未刷新"; ReadLabel.Text = error.Message; } }
        finally { _polling = false; }
    }
    private void PaintMode()
    {
        int? mode = _current?.Settings.FanMode;
        AutoMode.IsChecked = mode == 0; MaxMode.IsChecked = mode == 1; CustomMode.IsChecked = mode == 6;
        ModeLabel.Text = mode switch { 0 => "当前：自动", 1 => "当前：最大风量", 6 => "当前：自定义", _ => "模式未知" };
        if (_current is not null) ReadLabel.Text = $"模式记录更新于 {_current.CapturedAt:HH:mm:ss} · 两路风扇 · 编辑与实时状态独立";
    }
    private void PaintSession()
    {
        var status = _client.Status;
        if (_client.Session is { } session) SessionLabel.Text = "会话记录：" + session + (status?.CurveReturn is { } rc ? $"\n最近曲线返回值：{rc}；生效判据为两路运行时节点匹配。" : "");
        if (!_showSession || _busy || status is null) { UpdateEnabled(); return; }
        if (status.State == "Active")
        {
            TrialLabel.Text = status.Deadline is { } deadline ? $"试用中 · 约 {Math.Max(0, Math.Ceiling((deadline - DateTimeOffset.UtcNow).TotalSeconds))} 秒后恢复"
                : "运行期间持续保持 · 退出、心跳中断或睡眠恢复后还原";
            DraftLabel.Text = Changed ? "控制正在运行 · 新编辑尚未应用" : status.Deadline.HasValue ? "曲线试用中" : "自定义曲线已应用";
        }
        else if (status.State == "Restored") { TrialLabel.Text = status.Message; DraftLabel.Text = "已恢复进入前设置 · 编辑草稿保留"; }
        string key = status.State + status.Deadline + status.Message;
        if (_lastStatus != key)
        {
            _lastStatus = key;
            ShowResult(status.State switch { "Active" => status.Deadline.HasValue ? "曲线试用中" : "曲线已应用", "Restored" => "已恢复", "Restoring" => "正在恢复", _ => "需要核对" }, status.Message,
                status.State is "Active" or "Restored" ? InfoBarSeverity.Success : status.State == "RecoveryFailed" ? InfoBarSeverity.Error : InfoBarSeverity.Informational);
        }
        UpdateEnabled();
    }
    private void Signal()
    { UpdateEnabled(); if (_lastActive != TrialInProgress) { _lastActive = TrialInProgress; ActivityChanged?.Invoke(); } }
    private void UpdateEnabled()
    {
        bool ready = _baseline is not null && !_reading && !_busy;
        ReadButton.IsEnabled = !_reading && !_busy;
        CpuEditor.IsEnabled = GpuEditor.IsEnabled = ready;
        AutoMode.IsEnabled = MaxMode.IsEnabled = CustomMode.IsEnabled = ready;
        ResetButton.IsEnabled = ready;
        TryButton.IsEnabled = ready && Changed; TryButton.Visibility = _client.NeedsRecovery ? Visibility.Collapsed : Visibility.Visible;
        ApplyButton.IsEnabled = ready && (Changed || _current?.Settings.FanMode != 6 || !_client.NeedsRecovery);
        KeepButton.Visibility = _client.OwnsSession && _client.Status is { State: "Active", Deadline: not null } ? Visibility.Visible : Visibility.Collapsed;
        KeepButton.IsEnabled = !_busy;
        StopButton.Visibility = _client.NeedsRecovery ? Visibility.Visible : Visibility.Collapsed; StopButton.IsEnabled = !_busy;
    }
    private void ShowResult(string title, string message, InfoBarSeverity severity) { ResultBar.Title = title; ResultBar.Message = message; ResultBar.Severity = severity; ResultBar.IsOpen = true; }
    private void EditorsSizeChanged(object sender, SizeChangedEventArgs e)
    { bool narrow = e.NewSize.Width < 680; Grid.SetColumn(GpuEditor, narrow ? 0 : 1); Grid.SetRow(GpuEditor, narrow ? 1 : 0); Editors.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star); }
    public void Dispose() { _disposed = true; _poll.Stop(); _client.Dispose(); }
    internal bool ReadyForSmoke => _baseline is not null && CpuEditor.Curve is not null && GpuEditor.Curve is not null;
    internal bool ModesReadyForSmoke => ReadyForSmoke && AutoMode.IsChecked == (_current?.Settings.FanMode == 0) && AutoMode.IsEnabled && MaxMode.IsEnabled && CustomMode.IsEnabled;
    internal void ShowActionsForSmoke() => ApplyButton.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
    internal void EditForSmoke()
    {
        if (_baseline is null) throw new InvalidOperationException("曲线没有读取");
        var cpu = _baseline.Curves[0]; CpuEditor.EditNode(1, cpu.Nodes[1].Temperature + 2, cpu.Nodes[1].Percent + 3);
        FanCurveProtocol.Validate(_baseline, Proposed());
        if (CpuEditor.Curve!.Same(cpu) || CpuEditor.Curve.Nodes[0] != cpu.Nodes[0] || CpuEditor.Curve.Nodes[3] != cpu.Nodes[3] || !ApplyButton.IsEnabled || !TryButton.IsEnabled) throw new InvalidOperationException("曲线编辑或操作按钮异常");
    }
}
