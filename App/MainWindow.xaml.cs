using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using KClevoPanel.Core;

namespace KClevoPanel.App;
public sealed partial class MainWindow : Window
{
    private readonly System.Diagnostics.Stopwatch _startup = System.Diagnostics.Stopwatch.StartNew();
    private readonly double _processLaunchOffsetMs = ProcessAgeMilliseconds();
    private static double ProcessAgeMilliseconds()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return Math.Max(0, (DateTime.Now - process.StartTime).TotalMilliseconds);
    }
    private readonly bool _slowProviderProbe = Environment.GetCommandLineArgs().Contains("--smoke-test") && Environment.GetCommandLineArgs().Contains("--slow-providers");
    private double? _rootLoadedMs, _firstSystemMs, _firstCpuMs, _firstGpuMs, _firstThermalMs, _inventoryMs;
    private bool _inventoryReading, _gpuReading, _thermalReading, _gpuCompleted, _thermalCompleted, _loaded;
    private GpuReading _gpu = new(null, null, null, null, null, "初始化中", "NVML");
    private DateTimeOffset? _gpuCapturedAt;
    private string _cpuModel = StaticInventory.ReadCpuNameFast();
    private MachineInfo? _paintedMachine;
    private string _dllDetails = "设备信息正在后台读取…";
    private readonly WindowsTelemetry _telemetry = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DchuWorkerClient _firmware = new();
    private readonly DirectPresetClient _presets = new();
    private readonly DispatcherTimer _presetTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private PresetState _presetState = new(null, false, "读取固件档位…");
    private bool _presetReading, _presetChanging;
    private string? _presetError;
    private long _presetRequests;
    private string? _presetSuccess;
    private int? _presetSuccessMode;
    private DateTimeOffset _presetSuccessUntil;
    private ThermalReading? _thermal;
    private bool _reading, _closed, _paused;
    private MachineInfo? _machine;
    private Snapshot? _snapshot;
    private readonly SeriesHistory _cpuHistory = new(), _memoryHistory = new(), _gpuHistory = new(), _batteryHistory = new();
    private readonly SeriesHistory _cpuFanHistory = new(), _gpuFanHistory = new();
    private NativeTray? _tray;
    private MiniWindow? _mini;
    private bool _exiting;
    private string? _trayError;
    private void ThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Root is null || sender is not ComboBox chooser) return;
        Root.RequestedTheme = chooser.SelectedIndex switch { 1 => ElementTheme.Light, 2 => ElementTheme.Dark, _ => ElementTheme.Default };
        _mini?.SetTheme(Root.RequestedTheme);
        SavePreferences();
    }
    public MainWindow()
    {
        // Prime CPU counters while XAML initializes, instead of waiting a full sampling interval.
        _ = _telemetry.Read(includeGpu: false);
        InitializeComponent();
        InitializePreferences();
        Fans.ActivityChanged += PaintPreset;
        Fans.RefreshAvailabilityChanged += () => { if (_page == "fans") RefreshButton.IsEnabled = Fans.CanRefresh; };
        HardwareDetails.CurrentReadings = () => new { Machine = _machine, Snapshot = _snapshot, DchuState = _firmware.State, ReadOnlyCommands = new[] { 12, 13 } };
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        Title = "KClevoPanel";
        MainPresets.PresetRequested += ChangePreset;
        _presetTimer.Tick += async (_, _) => await RefreshPresetAsync();
        ResizeLogical(1100, 940, fitWorkArea: true);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleDragRegion);
        ((OverlappedPresenter)AppWindow.Presenter).SetBorderAndTitleBar(true, false);
        WindowChrome.RemoveSystemBorder(WinRT.Interop.WindowNative.GetWindowHandle(this));
        try
        {
            _tray = new NativeTray(WinRT.Interop.WindowNative.GetWindowHandle(this));
            _tray.PrimaryClick = ShowMain;
            _tray.ContextClick = ToggleMini;
            _tray.OpenMain = ShowMain; _tray.TogglePause = TogglePause; _tray.Quit = () => _ = ConfirmExitAsync(Root.XamlRoot);
            _tray.IsPaused = () => _paused;
            _tray.MinimizeRequested = HideToTray;
            _tray.PowerTransition = Fans.StopForPowerChange;
            if (!_tray.IsAdded) _trayError = "托盘暂不可用，请使用侧栏退出程序。";
        }
        catch (Exception e) { _trayError = "托盘初始化失败：" + e.Message; }
        AppWindow.Closing += (_, e) => { if (!_exiting) { e.Cancel = true; if (_tray?.IsAdded == true) HideToTray(); } };
        Navigation.SelectedItem = OverviewItem;
        _timer.Tick += async (_, _) => await RefreshAsync(false);
        _thermalTimer.Tick += async (_, _) => await RefreshThermalAsync(false);
        Root.Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            _rootLoadedMs = _startup.Elapsed.TotalMilliseconds;
            if (!_paused) { _timer.Start(); _thermalTimer.Start(); }
            await RefreshAsync(true);
            _presetTimer.Start(); _ = RefreshPresetAsync();
            string[] args = Environment.GetCommandLineArgs();
            if (args.Contains("--startup") && !IsSmokeRun && _tray?.IsAdded == true) HideToTray();
            int smoke = Array.IndexOf(args, "--smoke-test");
            if (smoke >= 0 && smoke + 1 < args.Length) await RunSmokeAsync(args[smoke + 1]);
        };
        Closed += (_, _) => { _closed = true; _timer.Stop(); _thermalTimer.Stop(); _presetTimer.Stop(); _presets.Dispose(); _firmware.Dispose(); Fans.Dispose(); SetChartsActive(false); _tray?.Dispose(); _mini?.Close(); HardwareDetails.CancelRead(); _ = Task.Run(_telemetry.Dispose); };
    }
    private void ResizeLogical(int width, int height, bool fitWorkArea = false)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(handle) / 96d;
        int w = (int)Math.Round(width * scale), h = (int)Math.Round(height * scale);
        if (fitWorkArea)
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            w = Math.Min(w, area.Width); h = Math.Min(h, area.Height);
        }
        AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
    private async Task RefreshPresetAsync()
    {
        if (_closed || _presetReading || _presetChanging) return;
        _presetReading = true;
        try { var state = await _presets.ReadAsync(); if (!_closed) { _presetState = state; if (state.Available) _presetError = null; PaintPreset(); } }
        finally { _presetReading = false; }
    }
    private async void ChangePreset(int mode)
    {
        if (_closed || _presetChanging || Fans.TrialInProgress || !_presetState.Available || mode == _presetState.Mode) return;
        _presetChanging = true; _presetError = null; _presetSuccess = null; _presetRequests++;
        PaintPreset(); PresetStatus.Text = "正在应用固件档位…";
        try
        {
            var result = await _presets.ChangeAsync(mode);
            if (_closed) return;
            _presetState = result;
            if (!result.Available || result.Mode != mode) _presetError = result.Status;
            else if (result.Verification == "CommandAcknowledgementAndSavedSelection")
            {
                _presetSuccess = "已切换至「" + new[] { "安静", "省电", "性能", "娱乐" }[mode] + "」";
                _presetSuccessMode = mode;
                _presetSuccessUntil = DateTimeOffset.Now.AddSeconds(4);
            }
        }
        finally { _presetChanging = false; if (!_closed) PaintPreset(); }
    }
    private void PaintPreset()
    {
        PresetProgress.IsActive = _presetChanging; PresetProgress.Visibility = _presetChanging ? Visibility.Visible : Visibility.Collapsed;
        MainPresets.SetState(_presetState.Mode, _presetState.Available && !_presetChanging && !Fans.TrialInProgress);
        string? success = !_presetChanging && _presetState.Available && _presetState.Mode == _presetSuccessMode && _presetError is null && DateTimeOffset.Now < _presetSuccessUntil ? _presetSuccess : null;
        PresetStatus.Text = _presetChanging ? "正在应用固件档位…" : _presetError ?? success ?? _presetState.Status;
        PresetSuccessIcon.Visibility = success is not null ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(MainPresets, PresetStatus.Text + "\n直接控制固件性能档位；风扇、Windows电源和GPU策略不随档位额外更改。" + (_presetState.JournalPath is { } log ? "\n事务日志：" + log : ""));
        _mini?.SetPreset(Fans.TrialInProgress ? _presetState with { Available = false, Status = "曲线试用期间暂不切换档位" } : _presetState, _presetChanging, _presetError, success);
    }
    private async Task RefreshAsync(bool inventory)
    {
        if (_closed) return;
        if (inventory) _ = RefreshInventoryAsync();
        _ = RefreshGpuAsync(inventory);
        if (inventory) _ = RefreshThermalAsync(true);
        if (_reading) return;
        _reading = true; if (_page == "overview") RefreshButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => _telemetry.Read(includeGpu: false));
            if (_closed) return;
            _snapshot = result;
            PaintLatest();
        }
        catch (Exception e)
        { if (!_closed) { ErrorBar.Message = $"读取失败：{e.Message}"; ErrorBar.IsOpen = true; UpdateLabel.Text = "本次读取失败；当前数值为上次结果"; } }
        finally { _reading = false; if (!_closed && _page == "overview") RefreshButton.IsEnabled = true; }
    }
    private async Task RefreshInventoryAsync()
    {
        if (_inventoryReading || _closed) return;
        _inventoryReading = true;
        try
        {
            if (_slowProviderProbe && _inventoryMs is null) await Task.Delay(4000);
            if (_closed) return;
            var machine = await Task.Run(StaticInventory.Read);
            if (_closed) return;
            _machine = machine; _inventoryMs ??= _startup.Elapsed.TotalMilliseconds;
            if (machine.Cpu != "不可用") _cpuModel = machine.Cpu;
            PaintLatest();
        }
        catch (Exception e) { if (!_closed) { ErrorBar.Message = "设备信息读取失败：" + e.Message; ErrorBar.IsOpen = true; } }
        finally { _inventoryReading = false; }
    }
    private async Task RefreshGpuAsync(bool force)
    {
        if (_gpuReading || _closed || (_paused && !force)) return;
        _gpuReading = true;
        try
        {
            if (_slowProviderProbe && !_gpuCompleted) await Task.Delay(4000);
            if (_closed) return;
            var gpu = await Task.Run(_telemetry.ReadGpu);
            if (_closed) return;
            _gpu = gpu; _gpuCapturedAt = DateTimeOffset.Now;
            _gpuCompleted = true; _firstGpuMs ??= _startup.Elapsed.TotalMilliseconds;
            PaintLatest();
        }
        catch (Exception e) { if (!_closed) { _gpuCompleted = true; _gpu = new(null, null, null, null, null, "GPU读取失败：" + e.Message, "NVML"); PaintLatest(); } }
        finally { _gpuReading = false; }
    }
    private async Task RefreshThermalAsync(bool force)
    {
        if (_thermalReading || _closed || (_paused && !force)) return;
        _thermalReading = true;
        try
        {
            if (_slowProviderProbe && !_thermalCompleted) await Task.Delay(4000);
            if (_closed) return;
            var thermal = await _firmware.ReadAsync();
            if (_closed) return;
            _thermal = thermal; _thermalCompleted = true;
            Fans.UpdateTelemetry(thermal);
            if (thermal is not null) { _firstThermalMs ??= _startup.Elapsed.TotalMilliseconds; _thermalSampleCount++; }
            PaintLatest();
        }
        catch (Exception e) { if (!_closed) { FanState.Text = "温度与风扇读取失败：" + e.Message; } }
        finally { _thermalReading = false; }
    }
    private void PaintLatest()
    {
        if (_closed || _snapshot is null) return;
        _snapshot = _snapshot with { Gpu = _gpu, GpuCapturedAt = _gpuCapturedAt, Thermal = _thermal };
        Paint(_snapshot, _machine);
        _firstSystemMs ??= _startup.Elapsed.TotalMilliseconds;
        if (_snapshot.CpuPercent.HasValue) _firstCpuMs ??= _startup.Elapsed.TotalMilliseconds;
    }
    private static string Number(double? value, string format, string unit) => value.HasValue ? value.Value.ToString(format) + unit : "不可用";
    private void Paint(Snapshot s, MachineInfo? m)
    {
        double? memoryPercent = s.MemoryTotalGiB > 0 ? s.MemoryUsedGiB / s.MemoryTotalGiB * 100 : null;
        _cpuHistory.Add(s.CapturedAt, s.CpuPercent); _memoryHistory.Add(s.CapturedAt, memoryPercent);
        _gpuHistory.Add(s.GpuCapturedAt ?? s.CapturedAt, s.Gpu.UtilizationPercent); _batteryHistory.Add(s.CapturedAt, s.BatteryPercent);
        CpuChart.Update(_cpuHistory, s.CapturedAt); MemoryChart.Update(_memoryHistory, s.CapturedAt);
        GpuChart.Update(_gpuHistory, s.GpuCapturedAt ?? s.CapturedAt); BatteryChart.Update(_batteryHistory, s.CapturedAt);
        CpuValue.Text = s.CpuPercent.HasValue ? Number(s.CpuPercent, "0.0", "%") : "—";
        CpuName.Text = DeviceLabels.CpuModel(_cpuModel);
        ToolTipService.SetToolTip(CpuName, _cpuModel);
        CpuTemperature.Text = DeviceLabels.Temperature(s.Thermal?.CpuTemperatureC);
        ToolTipService.SetToolTip(CpuTemperature, _thermalCompleted ? _firmware.State : "CPU 温度正在连接只读通道");
        var cpuFan = s.Thermal?.Fans.FirstOrDefault(f => f.Name == "CPU");
        var gpuFan = s.Thermal?.Fans.FirstOrDefault(f => f.Name == "GPU");
        CpuFanValue.Text = Number(cpuFan?.Rpm, "0", " RPM"); GpuFanValue.Text = Number(gpuFan?.Rpm, "0", " RPM");
        var thermalTime = s.Thermal?.CapturedAt ?? s.CapturedAt;
        _cpuFanHistory.Add(thermalTime, cpuFan?.Rpm); _gpuFanHistory.Add(thermalTime, gpuFan?.Rpm);
        CpuFanChart.Update(_cpuFanHistory, thermalTime, 10000); GpuFanChart.Update(_gpuFanHistory, thermalTime, 10000);
        FanState.Text = s.Thermal is null ? (_thermalCompleted ? _firmware.State : "正在核对设备并连接只读通道…") : $"CPU {Number(cpuFan?.DutyPercent,"0","%")} · GPU {Number(gpuFan?.DutyPercent,"0","%")} 占空比 · {_thermalTimer.Interval.TotalSeconds:0.#} 秒采样";
        MemoryValue.Text = Number(s.MemoryUsedGiB, "0.0", " GiB");
        MemoryDetail.Text = Number(memoryPercent, "0", "%") + " 已用 / 总容量 " + Number(s.MemoryTotalGiB, "0.0", " GiB");
        GpuValue.Text = s.Gpu.UtilizationPercent.HasValue ? Number(s.Gpu.UtilizationPercent, "0", "%") : "—";
        GpuTemperature.Text = DeviceLabels.Temperature(s.Gpu.TemperatureC);
        ToolTipService.SetToolTip(GpuTemperature, s.Gpu.State);
        GpuDetail.Text = DeviceLabels.GpuDetails(s.Gpu);
        GpuName.Text = DeviceLabels.GpuModel(s.Gpu.Name);
        ToolTipService.SetToolTip(GpuName, s.Gpu.Name ?? s.Gpu.State);
        BatteryValue.Text = Number(s.BatteryPercent, "0", "%");
        BatteryDetail.Text = (s.OnAc is null ? "供电状态未知" : s.OnAc.Value ? "已连接电源" : "使用电池")
            + (s.Charging is null ? "" : s.Charging.Value ? " · 充电中" : " · 未充电");
        UpdatePageStatus();
        if (m is not null && !ReferenceEquals(m, _paintedMachine))
        {
            _paintedMachine = m;
            HardwareDetails.SetMachine(m);
            _dllDetails = string.Join("\n\n", m.Dlls.Select(dll => dll.Path + "\n" + (dll.Error is not null ? "不可用：" + dll.Error : $"大小：{dll.Size:N0} 字节\nSHA256：{dll.Sha256}\n导出：{string.Join(", ", dll.Exports)}")));
        }
        var channel = new StringBuilder($"系统指标 / NVIDIA：每 {_timer.Interval.TotalMilliseconds:0} 毫秒 · Windows API / NVML\nCPU 温度 / 风扇：每 {_thermalTimer.Interval.TotalMilliseconds:0} 毫秒 · {_firmware.State} · 独立只读进程 12/13\n固件档位：{_presetState.Status} · 独立 DCHU 接口\n充电上限：未提供控制\n\n");
        channel.AppendLine(_dllDetails);
        ChannelDetails.Text = channel.ToString();
        var errors = (m?.Errors ?? []).Concat(s.Errors).Concat(_trayError is null ? [] : new[] { _trayError })
            .Concat(_presetError is null ? [] : new[] { _presetError }).ToArray();
        ErrorBar.IsOpen = errors.Length > 0; ErrorBar.Message = string.Join("；", errors);
        if (_mini?.IsShown == true) _mini.Paint(s, _cpuModel, _cpuHistory, _memoryHistory, _gpuHistory, _batteryHistory, _paused);
        _tray?.UpdateTip($"KClevoPanel · CPU {Number(s.CpuPercent,"0","%")} · GPU {Number(s.Gpu.TemperatureC,"0","°C")} · 电池 {Number(s.BatteryPercent,"0","%")}");
    }
    private void NavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (OverviewPanel is null) return;
        string page = (args.SelectedItem as NavigationViewItem)?.Tag?.ToString() ?? "overview";
        ShowPage(page);
    }
    private void UpdatePageStatus()
    {
        if (_page == "settings") UpdateLabel.Text = "按你的习惯，调整面板体验";
        else if (_page == "channels") UpdateLabel.Text = "查看本机数据来源与接口状态";
        else if (_page == "about") UpdateLabel.Text = "关于应用与这台设备";
        else if (_page == "device") UpdateLabel.Text = "本机硬件、设备规格与驱动信息";
        else if (_page == "fans") UpdateLabel.Text = "调整风扇温度控制";
        else if (_snapshot is null) UpdateLabel.Text = "准备读取…";
        else UpdateLabel.Text = _paused ? $"已暂停 · 最后更新 {_snapshot.CapturedAt:HH:mm:ss}" : $"更新于 {_snapshot.CapturedAt:HH:mm:ss} · {SamplingDescription}";
    }
    private async void RefreshClicked(object sender, RoutedEventArgs e)
    {
        if (_page == "fans") await Fans.RefreshAsync();
        else if (DevicePanel.Visibility == Visibility.Visible) await HardwareDetails.RefreshAsync();
        else await RefreshAsync(true);
    }
    private void TogglePause() => SetPaused(!_paused);
    private void SetPaused(bool value)
    {
        _paused = value;
        foreach (var chart in MainCharts()) chart.SetPaused(value);
        if (_paused) { _timer.Stop(); _thermalTimer.Stop(); } else { _telemetry.ResetCpuBaseline(); _timer.Start(); _thermalTimer.Start(); }
        if (_paused) UpdateLabel.Text = $"已暂停 · 最后更新 {_snapshot?.CapturedAt:HH:mm:ss}";
        else UpdateLabel.Text = "继续采样…";
        if (_snapshot is { } s) _mini?.Paint(s, _cpuModel, _cpuHistory, _memoryHistory, _gpuHistory, _batteryHistory, _paused);
        UpdatePageStatus();
    }
    private void MinimizeClicked(object sender, RoutedEventArgs e) => HideToTray();
    private void MaximizeClicked(object sender, RoutedEventArgs e)
    {
        var p = (OverlappedPresenter)AppWindow.Presenter;
        if (p.State == OverlappedPresenterState.Maximized) p.Restore(); else p.Maximize();
    }
    private void HideClicked(object sender, RoutedEventArgs e) { if (_tray?.IsAdded == true) HideToTray(); else ExitApplication(); }
    private IEnumerable<HistoryChart> MainCharts() => new[] { CpuChart, MemoryChart, GpuChart, BatteryChart, CpuFanChart, GpuFanChart };
    private void SetChartsActive(bool active) { foreach (var chart in MainCharts()) chart.SetActive(active); }
    private void HideToTray()
    {
        if (_tray?.IsAdded == true) { SetChartsActive(false); AppWindow.Hide(); }
        else ((OverlappedPresenter)AppWindow.Presenter).Minimize();
    }
    private void ShowMain()
    {
        if (((OverlappedPresenter)AppWindow.Presenter).State == OverlappedPresenterState.Minimized) ((OverlappedPresenter)AppWindow.Presenter).Restore();
        AppWindow.Show(); SetChartsActive(true); Activate();
    }
    private int _activationRequests;
    internal void ActivateExisting() { if (!_closed) { _activationRequests++; ShowMain(); } }
    private void ToggleMini() { if (_mini?.IsShown == true) _mini.AppWindow.Hide(); else ShowMini(); }
    private void ShowMini()
    {
        if (_tray is null) return;
        _mini ??= new MiniWindow(ShowMain, ChangePreset, root => _ = ConfirmExitAsync(root));
        _mini.SetTheme(Root.RequestedTheme);
        _mini.SetPreset(Fans.TrialInProgress ? _presetState with { Available = false, Status = "曲线试用期间暂不切换档位" } : _presetState, _presetChanging, _presetError, _presetState.Mode == _presetSuccessMode && DateTimeOffset.Now < _presetSuccessUntil ? _presetSuccess : null);
        if (_snapshot is { } s) _mini.Paint(s, _cpuModel, _cpuHistory, _memoryHistory, _gpuHistory, _batteryHistory, _paused);
        _mini.ShowAt(_tray.Anchor());
    }
    private bool _exitConfirmationOpen;
    private async void NavigationInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs e)
    { if (e.InvokedItemContainer == ExitItem) await ConfirmExitAsync(Root.XamlRoot); }
    private ContentDialog CreateExitDialog(XamlRoot root)
    {
        var dialog = new ContentDialog { XamlRoot = root, Title = "退出 KClevoPanel？", Content = "退出后将停止监测。正在运行的自定义风扇会话将请求恢复进入前的设置。", PrimaryButtonText = "退出程序", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, RequestedTheme = Root.RequestedTheme };
        dialog.Resources["AccentFillColorDefaultBrush"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 28));
        dialog.Resources["AccentFillColorSecondaryBrush"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 180, 40, 25));
        dialog.Resources["TextOnAccentFillColorPrimaryBrush"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
        return dialog;
    }
    private async Task ConfirmExitAsync(XamlRoot root)
    {
        if (_exitConfirmationOpen || _exiting) return;
        _exitConfirmationOpen = true; if (_mini is not null) _mini.HoldOpen = true;
        try { if (await CreateExitDialog(root).ShowAsync() == ContentDialogResult.Primary) ExitApplication(); }
        finally { _exitConfirmationOpen = false; if (_mini is not null) _mini.HoldOpen = false; }
    }
    private void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        Fans.StopForExit();
        _mini?.Close(); _mini = null;
        Close();
    }
}
