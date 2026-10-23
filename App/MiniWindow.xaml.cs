using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using KClevoPanel.Core;

namespace KClevoPanel.App;
public sealed partial class MiniWindow : Window
{
    private readonly Action _open;
    private string? _presetHint;
    private string _fanSummary = "风扇通道连接中";
    public IntPtr Handle { get; }
    public bool IsShown => AppWindow.IsVisible;
    internal bool PresetLayoutVerifiedForSmoke => MiniPresets.LayoutVerifiedForSmoke;
    internal string PresetLayoutDetailsForSmoke => MiniPresets.LayoutDetailsForSmoke;
    internal bool FeedbackVisibleForSmoke => PresetSuccessIcon.Visibility == Visibility.Visible;
    internal FrameworkElement RenderRoot => MiniRoot;
    internal void SetTheme(ElementTheme theme) => MiniRoot.RequestedTheme = theme;
    internal bool ProcessorLabelsMatch(string cpuModel, string gpuModel, string cpuTemperature, string gpuTemperature)
        => CpuModel.Text == cpuModel && GpuModel.Text == gpuModel && CpuTemperature.Text == cpuTemperature && GpuTemperature.Text == gpuTemperature
        && MainWindow.ProcessorRowAligned(CpuModel, CpuTemperature, CpuGraph, MiniRoot)
        && MainWindow.ProcessorRowAligned(GpuModel, GpuTemperature, GpuGraph, MiniRoot);
    public MiniWindow(Action open, Action<int> preset)
    {
        InitializeComponent(); _open = open; MiniPresets.PresetRequested += preset;
        SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
        Title = "KClevoPanel 快速面板";
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false); presenter.IsResizable = false;
        presenter.IsMinimizable = false; presenter.IsMaximizable = false; presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter); AppWindow.IsShownInSwitchers = false;
        presenter.SetBorderAndTitleBar(false, false);
        WindowChrome.ConfigurePopup(Handle);
        Activated += (_, e) =>
        {
            WindowChrome.ConfigurePopup(Handle);
            DispatcherQueue.TryEnqueue(() => WindowChrome.ConfigurePopup(Handle));
            if (e.WindowActivationState == WindowActivationState.Deactivated) { foreach (var chart in MiniCharts()) chart.SetActive(false); AppWindow.Hide(); }
        };
        Closed += (_, _) => { foreach (var chart in MiniCharts()) chart.SetActive(false); };
    }
    public void ShowAt(RectInt32 anchor)
    {
        var display = DisplayArea.GetFromPoint(new PointInt32(anchor.X, anchor.Y), DisplayAreaFallback.Nearest);
        var area = display.WorkArea;
        // Move first so DPI comes from the taskbar's monitor rather than the main window.
        AppWindow.Move(new PointInt32(area.X, area.Y));
        double scale = GetDpiForWindow(Handle) / 96.0;
        int width = (int)Math.Round(460 * scale), height = (int)Math.Round(488 * scale);
        int x = Math.Clamp(anchor.X + anchor.Width - width, area.X, Math.Max(area.X, area.X + area.Width - width));
        int y = anchor.Y >= area.Y + area.Height ? anchor.Y - height - 8 : anchor.Y + anchor.Height + 8;
        y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Y + area.Height - height));
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        WindowChrome.ConfigurePopup(Handle);
        AppWindow.Show(); foreach (var chart in MiniCharts()) chart.SetActive(true); Activate();
        WindowChrome.ConfigurePopup(Handle);
    }
    public void Paint(Snapshot s, string cpuModel, SeriesHistory cpu, SeriesHistory memory, SeriesHistory gpu, SeriesHistory battery, bool paused)
    {
        static string Percent(double? value) => value.HasValue ? $"{value:0}%" : "—";
        Cpu.Text = Percent(s.CpuPercent);
        CpuModel.Text = DeviceLabels.CpuModel(cpuModel);
        GpuModel.Text = DeviceLabels.GpuModel(s.Gpu.Name);
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(CpuModel, cpuModel);
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(GpuModel, s.Gpu.Name ?? s.Gpu.State);
        CpuTemperature.Text = DeviceLabels.Temperature(s.Thermal?.CpuTemperatureC);
        GpuTemperature.Text = DeviceLabels.Temperature(s.Gpu.TemperatureC);
        double? memoryPercent = s.MemoryTotalGiB > 0 ? s.MemoryUsedGiB / s.MemoryTotalGiB * 100 : null;
        Memory.Text = Percent(memoryPercent); Gpu.Text = Percent(s.Gpu.UtilizationPercent); Battery.Text = Percent(s.BatteryPercent);
        MemoryDetail.Text = s.MemoryTotalGiB.HasValue ? $"{s.MemoryUsedGiB:0.0} / {s.MemoryTotalGiB:0.0} GiB" : "物理内存";
        BatteryDetail.Text = s.OnAc == true ? "已连接电源" : s.OnAc == false ? "使用电池" : "供电状态读取中";
        CpuGraph.Update(cpu, s.CapturedAt); MemoryGraph.Update(memory, s.CapturedAt);
        GpuGraph.Update(gpu, s.GpuCapturedAt ?? s.CapturedAt); BatteryGraph.Update(battery, s.CapturedAt);
        foreach (var chart in MiniCharts()) chart.SetPaused(paused);
        Stamp.Text = paused ? "已暂停" : s.CapturedAt.ToString("HH:mm:ss");
        Details.Text = "GPU " + DeviceLabels.GpuDetails(s.Gpu);
        _fanSummary = s.Thermal is { } t ? $"风扇 CPU {t.Fans.FirstOrDefault(f => f.Name == "CPU")?.Rpm:0} / GPU {t.Fans.FirstOrDefault(f => f.Name == "GPU")?.Rpm:0} RPM" : "风扇通道连接中或暂不可用";
        Thermals.Text = _presetHint ?? _fanSummary;
    }
    private IEnumerable<HistoryChart> MiniCharts() => new[] { CpuGraph, MemoryGraph, GpuGraph, BatteryGraph };
    private void OpenClicked(object sender, RoutedEventArgs e) { AppWindow.Hide(); _open(); }
    internal void SetPreset(PresetState state, bool busy, string? error, string? success = null)
    {
        MiniPresets.SetState(state.Mode, state.Available && !busy);
        _presetHint = busy ? "正在应用固件档位…" : error ?? success ?? (!state.Available ? state.Status : null);
        PresetSuccessIcon.Visibility = success is not null && !busy && error is null && state.Available ? Visibility.Visible : Visibility.Collapsed;
        Thermals.Text = _presetHint ?? _fanSummary;
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(MiniPresets, (busy ? "正在应用固件档位…" : error ?? state.Status) + "\n直接控制固件性能档位；不额外更改风扇、Windows电源或GPU策略。");
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", ExactSpelling = true)] private static extern uint GetDpiForWindow(IntPtr hwnd);
}
