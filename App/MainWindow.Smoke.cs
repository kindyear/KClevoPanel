using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace KClevoPanel.App;
public sealed partial class MainWindow
{
    // Development-only smoke path. Exercises the actual window and read-only backends.
    private async Task RunSmokeAsync(string directory)
    {
        string path = Path.GetFullPath(directory);
        Directory.CreateDirectory(path);
        try
        {
            ResizeLogical(1100, 940);
            await Task.Delay(400);
            bool verifyFeedback = Environment.GetCommandLineArgs().Contains("--verify-preset-feedback");
            bool verifyEdges = Environment.GetCommandLineArgs().Contains("--verify-chart-edges");
            for (int i = 0; i < (verifyEdges ? 40 : _slowProviderProbe ? 10 : 6); i++) { await Task.Delay(1600); await RefreshAsync(false); }
            for (int i = 0; i < 40 && (_machine is null || !_gpuCompleted || !_thermalCompleted); i++) await Task.Delay(250);
            if (verifyEdges && (!CpuFanChart.FullWindowEdgesCovered || !GpuFanChart.FullWindowEdgesCovered))
                throw new InvalidOperationException("风扇图表完整60秒窗口的边界未连接或端点被裁切");
            _timer.Stop();
            if (_machine is null || _snapshot is null || _snapshot.MemoryTotalGiB is null || _snapshot.CpuPercent is null)
                throw new InvalidOperationException("系统数据未完成读取");
            if (_snapshot.Thermal is not { BoundsIntact: true } thermal || thermal.Fans.Length != 2 || thermal.CpuTemperatureC is null)
                throw new InvalidOperationException("温度与风扇未完成受保护读取");
            if (_snapshot.Gpu.TemperatureC is { } gpuTemp && thermal.GpuTemperatureC is { } ecGpu && Math.Abs(gpuTemp - ecGpu) > 8)
                throw new InvalidOperationException("EC温度与NVIDIA温度差异过大");
            bool independentStartup = _firstSystemMs < _firstGpuMs && _firstSystemMs < _firstThermalMs && _firstSystemMs < _inventoryMs
                && _firstCpuMs < _firstGpuMs && _firstCpuMs < _firstThermalMs && _firstCpuMs < _inventoryMs;
            if (_slowProviderProbe && !independentStartup) throw new InvalidOperationException("慢速读取仍阻塞系统指标");
            if (!ProcessorRowAligned(CpuName, CpuTemperature, CpuChart, Root) || !ProcessorRowAligned(GpuName, GpuTemperature, GpuChart, Root))
                throw new InvalidOperationException("主窗口型号与温度位置不一致");
            long beforeFrames = CpuChart.FrameCount;
            await Task.Delay(1100);
            long animatedFrames = CpuChart.FrameCount - beforeFrames;
            if (animatedFrames < 10) throw new InvalidOperationException("折线图刷新未与采样独立运行");
            // Keep real sampling active while taking theme previews, so the previews don't show a test-induced outage.
            _timer.Start();
            await RefreshAsync(false);
            await CaptureAsync(path, "overview.png");
            var originalTheme = Root.RequestedTheme;
            ThemeChooser.SelectedIndex = 1;
            await Task.Delay(400);
            if (Root.ActualTheme != ElementTheme.Light) throw new InvalidOperationException("浅色主题未生效");
            await CaptureAsync(path, "overview-light.png");
            await CaptureMainNativeAsync(path, "overview-light-native.png");
            ThemeChooser.SelectedIndex = 2;
            await Task.Delay(400);
            if (Root.ActualTheme != ElementTheme.Dark) throw new InvalidOperationException("深色主题未生效");
            await CaptureAsync(path, "overview-dark.png");
            await CaptureMainNativeAsync(path, "overview-dark-native.png");
            if (!MainPresets.LayoutVerifiedForSmoke) throw new InvalidOperationException("档位按钮间距、图标空间或选中背景宽度异常");
            if (verifyFeedback)
            {
                _presetSuccessMode = _presetState.Mode;
                _presetSuccess = "提示预览 · 已切换至「性能」";
                _presetSuccessUntil = DateTimeOffset.Now.AddSeconds(1);
                PaintPreset();
                if (PresetSuccessIcon.Visibility != Visibility.Visible) throw new InvalidOperationException("成功提示未显示");
                await CaptureMainNativeAsync(path, "success-preview.png");
                await Task.Delay(1100); PaintPreset();
                if (PresetSuccessIcon.Visibility != Visibility.Collapsed) throw new InvalidOperationException("成功提示未自动消失");
                _presetSuccess = null;
            }
            ThemeChooser.SelectedIndex = 0;
            Root.RequestedTheme = originalTheme;
            bool customTitleBar = ExtendsContentIntoTitleBar && !((OverlappedPresenter)AppWindow.Presenter).HasTitleBar;
            if (!customTitleBar) throw new InvalidOperationException("原生标题栏未移除");
            if (_tray?.IsAdded != true) throw new InvalidOperationException("托盘图标未注册");
            MinimizeClicked(this, new RoutedEventArgs());
            if (AppWindow.IsVisible) throw new InvalidOperationException("最小化按钮未隐藏窗口");
            ShowMain(); _tray.RequestMinimizeForSmoke();
            if (AppWindow.IsVisible) throw new InvalidOperationException("系统最小化消息未隐藏窗口");
            ShowMain();
            if (!_tray.CaptionDragHitForSmoke()) throw new InvalidOperationException("自定义标题栏未返回拖动命中区域");
            // Exercise Windows' close message rather than just hiding the window ourselves.
            _tray.RequestCloseForSmoke();
            if (_closed || AppWindow.IsVisible) throw new InvalidOperationException("关闭窗口未收进托盘");
            var beforeHiddenRead = _snapshot.CapturedAt;
            await Task.Delay(1200); await RefreshAsync(false);
            if (_snapshot.CapturedAt <= beforeHiddenRead) throw new InvalidOperationException("后台采样未继续");
            _tray.SimulatePrimaryClickForSmoke();
            await Task.Delay(600);
            if (_mini?.IsShown != true) throw new InvalidOperationException("托盘点击未打开小面板");
            if (!_mini.ProcessorLabelsMatch(CpuName.Text, GpuName.Text, CpuTemperature.Text, GpuTemperature.Text))
                throw new InvalidOperationException("托盘CPU/GPU型号、温度与主窗口不一致或排版溢出");
            await File.WriteAllTextAsync(Path.Combine(path,"chrome-info.txt"), WindowChrome.PopupStyleSummary(_mini.Handle));
            if (!WindowChrome.BorderRequestSucceeded(_mini.Handle)) throw new InvalidOperationException("小面板系统边框关闭请求失败");
            if (!WindowChrome.PopupFrameRemoved(_mini.Handle)) throw new InvalidOperationException("小面板经典窗口边框未移除");
            if (!_mini.PresetLayoutVerifiedForSmoke) throw new InvalidOperationException("小面板档位间距或图标空间异常");
            if (verifyFeedback)
            {
                _presetSuccess = "提示预览 · 已切换至「性能」";
                _presetSuccessMode = _presetState.Mode; _presetSuccessUntil = DateTimeOffset.Now.AddSeconds(2); PaintPreset();
                if (!_mini.FeedbackVisibleForSmoke) throw new InvalidOperationException("小面板成功提示未显示");
                await CaptureElementAsync(_mini.RenderRoot, path, "mini-success-preview.png");
                _presetSuccess = null; PaintPreset();
            }
            await CaptureElementAsync(_mini.RenderRoot, path, "mini.png");
            await CaptureElementAsync(_mini.RenderRoot, path, "mini-native.png");
            SetPaused(true);
            if (_timer.IsEnabled || _thermalTimer.IsEnabled || !_paused) throw new InvalidOperationException("暂停未停止采样");
            SetPaused(false); _timer.Stop();
            ShowMain();
            await Task.Delay(400);
            if (_mini.IsShown) throw new InvalidOperationException("小面板失焦后未收起");
            if (HardwareDetails.Report is not null) throw new InvalidOperationException("硬件详情在访问页面前已启动查询");
            Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(x => x.Tag?.ToString() == "device");
            await HardwareDetails.EnsureLoadedAsync();
            if (HardwareDetails.Report is not { Categories.Length: 8 } hardware || hardware.Categories.Any(c => c.Errors.Length > 0))
                throw new InvalidOperationException("硬件分类未完整读取");
            if (hardware.Categories.First(c => c.Key == "cpu").Devices.Length == 0 || hardware.Categories.First(c => c.Key == "memory").Devices.Length == 0
                || hardware.Categories.First(c => c.Key == "storage").Devices.Length == 0) throw new InvalidOperationException("核心硬件清单为空");
            ThemeChooser.SelectedIndex = 2;
            await Task.Delay(400);
            if (HardwareDetails.ColumnCountForSmoke != 2) throw new InvalidOperationException("宽窗口未使用双列硬件卡片");
            if (!HardwareDetails.CardsPackedForSmoke) throw new InvalidOperationException("双列硬件卡片仍留有行高空洞");
            await CaptureAsync(path, "device.png");
            await CaptureMainNativeAsync(path, "device-native.png");
            HardwareDetails.ExpandForSmoke("memory");
            HardwareDetails.CategoryForSmoke("memory").StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.05 });
            await Task.Delay(400);
            if (!HardwareDetails.CardsPackedForSmoke) throw new InvalidOperationException("展开规格后卡片未紧接排列");
            await CaptureMainNativeAsync(path, "device-memory-native.png");
            ThemeChooser.SelectedIndex = 1;
            ContentScroll.ChangeView(null, 0, null, true);
            await Task.Delay(400);
            await CaptureMainNativeAsync(path, "device-light-native.png");
            ResizeLogical(820, 940);
            await Task.Delay(600);
            if (HardwareDetails.ColumnCountForSmoke != 1) throw new InvalidOperationException("窄窗口未自动切换单列卡片");
            if (!HardwareDetails.CardsPackedForSmoke) throw new InvalidOperationException("单列硬件卡片间距异常");
            await CaptureMainNativeAsync(path, "device-narrow-native.png");
            ResizeLogical(1100, 940);
            ThemeChooser.SelectedIndex = 0; Root.RequestedTheme = originalTheme;
            Navigation.SelectedItem = FanItem;
            for (int i = 0; i < 120 && !Fans.ReadyForSmoke; i++) await Task.Delay(250);
            if (!Fans.ReadyForSmoke || !Fans.ModesReadyForSmoke || FanPanel.Visibility != Visibility.Visible || HeaderActions.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("散热控制页面未完成只读曲线加载");
            Fans.EditForSmoke();
            ThemeChooser.SelectedIndex = 2; await Task.Delay(400);
            await CaptureMainNativeAsync(path, "fan-curves-dark.png");
            ThemeChooser.SelectedIndex = 1; await Task.Delay(400);
            await CaptureMainNativeAsync(path, "fan-curves-light.png");
            Fans.ShowActionsForSmoke(); await Task.Delay(300);
            await CaptureMainNativeAsync(path, "fan-curves-actions.png");
            ContentScroll.ChangeView(null, 0, null, true);
            ResizeLogical(820, 940); await Task.Delay(500);
            await CaptureMainNativeAsync(path, "fan-curves-narrow.png");
            ResizeLogical(1100, 940);
            Navigation.SelectedItem = SettingsItem;
            ThemeChooser.SelectedIndex = 2;
            await Task.Delay(400);
            if (SettingsPanel.Visibility != Visibility.Visible || HeaderActions.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("设置页面入口或顶部操作可见性异常");
            SystemFrequencyChooser.SelectedIndex = 3; ThermalFrequencyChooser.SelectedIndex = 0;
            if (_timer.Interval.TotalMilliseconds != 2000 || _thermalTimer.Interval.TotalMilliseconds != 1000)
                throw new InvalidOperationException("采样频率设置未应用到独立计时器");
            _timer.Start();
            long beforeThermalSamples = _thermalSampleCount;
            await Task.Delay(2500);
            if (_thermalSampleCount <= beforeThermalSamples) throw new InvalidOperationException("温度采样仍受系统频率阻塞");
            SystemFrequencyChooser.SelectedIndex = 1;
            ThermalFrequencyChooser.SelectedIndex = 1;
            if (_thermalTimer.Interval.TotalMilliseconds != 2000) throw new InvalidOperationException("温度间隔未更新");
            ThermalFrequencyChooser.SelectedIndex = 0;
            var preferenceTest = new Preferences(2, 1000, 2000, false);
            if (JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(preferenceTest)) != preferenceTest
                || new Preferences(9, 17, 1).Normalize() != new Preferences())
                throw new InvalidOperationException("设置序列化或非法频率回退异常");
            ChannelExpander.IsExpanded = false;
            await Task.Delay(300);
            await CaptureMainNativeAsync(path, "settings-dark.png");
            ThemeChooser.SelectedIndex = 1; await Task.Delay(300);
            await CaptureMainNativeAsync(path, "settings-light.png");
            ThemeChooser.SelectedIndex = 2;
            ChannelExpander.IsExpanded = true; await Task.Delay(300);
            ChannelExpander.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            await Task.Delay(300); await CaptureMainNativeAsync(path, "settings-channels.png");
            AboutClicked(this, new RoutedEventArgs()); await Task.Delay(400);
            if (AboutPanel.Visibility != Visibility.Visible || _page != "about") throw new InvalidOperationException("关于页面导航失败");
            await CaptureMainNativeAsync(path, "about-dark.png");
            ThemeChooser.SelectedIndex = 1; await Task.Delay(300);
            await CaptureMainNativeAsync(path, "about-light.png");
            BackToSettingsClicked(this, new RoutedEventArgs());
            if (_page != "settings") throw new InvalidOperationException("关于返回设置失败");
            Navigation.IsPaneOpen = false; await Task.Delay(500);
            if (Navigation.IsPaneOpen || SidebarBrand.Visibility != Visibility.Collapsed) throw new InvalidOperationException("侧栏未收起");
            Navigation.SelectedItem = OverviewItem; await Task.Delay(300);
            await CaptureMainNativeAsync(path, "sidebar-collapsed.png");
            Navigation.IsPaneOpen = true; await Task.Delay(500);
            if (SidebarBrand.Visibility != Visibility.Visible) throw new InvalidOperationException("侧栏未展开");
            ThemeChooser.SelectedIndex = 0; Root.RequestedTheme = originalTheme;
            await File.WriteAllTextAsync(Path.Combine(path, "ui-smoke.json"), JsonSerializer.Serialize(new
            {
                Passed = true, Machine = _machine, Snapshot = _snapshot,
                CpuText = CpuValue.Text, GpuText = GpuValue.Text, MemoryText = MemoryValue.Text,
                BatteryText = BatteryValue.Text, CpuTemperatureText = CpuTemperature.Text,
                CpuFanText = CpuFanValue.Text, GpuFanText = GpuFanValue.Text,
                DchuMode = "IsolatedWorker-GetDCHU_Data_Buffer-12-13",
                CustomTitleBar = customTitleBar, TrayRegistered = _tray.IsAdded, TrayMessageOpenedMini = true,
                MiniDismissedOnDeactivation = true, CpuHistorySamples = _cpuHistory.Points.Count,
                PauseStoppedTimer = true, CaptionDragHit = true, CloseHidesToTray = true, HiddenWindowContinuesSampling = true,
                MinimizeHidesToTray = true, SystemMinimizeHidesToTray = true, PopupBorderSuppressionRequestSucceeded = true, PopupClassicFrameRemoved = true,
                SampleIntervalMilliseconds = _timer.Interval.TotalMilliseconds, AnimationFramesIn1100ms = animatedFrames,
                LightAndDarkThemesVerified = true, Backdrop = SystemBackdrop?.GetType().Name,
                FullSixtySecondFanEdgesVerified = verifyEdges,
                ProcessorLabelsAndPositionsVerified = true,
                Hardware = hardware, HardwareLoadedOnDemand = true, HardwareResponsiveColumnsVerified = true,
                HardwareCardGapsVerified = true,
                SettingsAndAboutNavigationVerified = true, SidebarCollapseExpandVerified = true,
                IndependentSamplingIntervalsVerified = true, SettingsSerializationAndValidationVerified = true,
                FanCurveReadAndEditVerified = true, FanModeControlsVerified = true, FanFirmwareWriteRequestsInSmoke = Fans.WriteRequests,
                Preset = _presetState, PresetWriteRequestsInSmoke = _presetRequests,
                PresetGapsAndIconSpaceVerified = true, SuccessFeedbackPreviewAndExpiryVerified = verifyFeedback,
                PreviewCaptureMode = "OwnXamlTreeWithOpaqueThemeBackground",
                Startup = new { RootLoadedMilliseconds = _rootLoadedMs, FirstSystemPaintMilliseconds = _firstSystemMs,
                    FirstCpuUsageMilliseconds = _firstCpuMs, FirstGpuMilliseconds = _firstGpuMs,
                    FirstThermalMilliseconds = _firstThermalMs, InventoryMilliseconds = _inventoryMs,
                    ProcessStartToFirstSystemMilliseconds = _processLaunchOffsetMs + _firstSystemMs,
                    ProcessStartToFirstCpuMilliseconds = _processLaunchOffsetMs + _firstCpuMs,
                    SimulatedSlowProviderDelayMilliseconds = _slowProviderProbe ? 4000 : 0, SystemAndCpuBeforeAllSlowProviders = independentStartup }
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        { await File.WriteAllTextAsync(Path.Combine(path, "ui-smoke.json"), JsonSerializer.Serialize(new { Passed = false, Error = e.ToString(), DchuState = _firmware.State, ThermalCompleted = _thermalCompleted, ThermalReading = _thermalReading, Preset = _presetState, PresetWriteRequestsInSmoke = _presetRequests, Width = Root.ActualWidth, Scale = Root.XamlRoot.RasterizationScale })); }
        finally { ExitApplication(); }
    }
    private async Task CaptureAsync(string directory, string name)
        => await CaptureElementAsync(Root, directory, name);
    internal static bool ProcessorRowAligned(FrameworkElement model, FrameworkElement temperature, FrameworkElement chart, FrameworkElement root)
    {
        var m = model.TransformToVisual(root).TransformPoint(new());
        var t = temperature.TransformToVisual(root).TransformPoint(new());
        var c = chart.TransformToVisual(root).TransformPoint(new());
        return Math.Abs(m.Y - t.Y) < 1 && m.X < t.X && model.ActualWidth > 0
            && Math.Abs(t.X + temperature.ActualWidth - (c.X + chart.ActualWidth)) < 1
            && m.Y + model.ActualHeight <= c.Y + 1;
    }
    private async Task CaptureMainNativeAsync(string directory, string name)
    {
        // Render only our XAML tree: desktop overlays must never become part of a page preview.
        await CaptureAsync(directory, name);
    }
    private static async Task CaptureElementAsync(UIElement element, string directory, string name)
    {
        var target = new RenderTargetBitmap();
        var background = new Microsoft.UI.Xaml.Media.SolidColorBrush((element as FrameworkElement)?.ActualTheme == ElementTheme.Light
            ? Windows.UI.Color.FromArgb(255, 243, 243, 243) : Windows.UI.Color.FromArgb(255, 32, 32, 32));
        Microsoft.UI.Xaml.Media.Brush? previous = null;
        if (element is Panel panel) { previous = panel.Background; panel.Background = background; }
        else if (element is Border border) { previous = border.Background; border.Background = background; }
        try { await target.RenderAsync(element); }
        finally
        {
            if (element is Panel savedPanel) savedPanel.Background = previous;
            else if (element is Border savedBorder) savedBorder.Background = previous;
        }
        var pixels = await target.GetPixelsAsync();
        var folder = await StorageFolder.GetFolderFromPathAsync(directory);
        var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)target.PixelWidth, (uint)target.PixelHeight, 96, 96, pixels.ToArray());
        await encoder.FlushAsync();
    }
}
