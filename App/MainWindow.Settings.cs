using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KClevoPanel.App;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer _thermalTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private long _thermalSampleCount;
    private bool _settingsReady;
    private string _page = "overview";
    private static readonly int[] SystemIntervals = [250, 500, 1000, 2000];
    private static readonly int[] ThermalIntervals = [1000, 2000, 5000];
    private bool IsSmokeRun => Environment.GetCommandLineArgs().Contains("--smoke-test");
    private string SamplingDescription => $"系统 {_timer.Interval.TotalSeconds:0.##} 秒 / 温度与风扇 {_thermalTimer.Interval.TotalSeconds:0.#} 秒";
    private sealed record Preferences(int Theme = 0, int SystemMilliseconds = 500, int ThermalMilliseconds = 1000, bool PaneOpen = true)
    {
        public Preferences Normalize() => new(Theme is >= 0 and <= 2 ? Theme : 0,
            SystemIntervals.Contains(SystemMilliseconds) ? SystemMilliseconds : 500,
            ThermalIntervals.Contains(ThermalMilliseconds) ? ThermalMilliseconds : 1000, PaneOpen);
    }
    private static string PreferencesPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KClevoPanel", "settings.json");
    private void InitializePreferences()
    {
        var saved = new Preferences();
        if (!IsSmokeRun)
        {
            try { if (File.Exists(PreferencesPath)) saved = (JsonSerializer.Deserialize<Preferences>(File.ReadAllText(PreferencesPath)) ?? saved).Normalize(); }
            catch { }
        }
        ThemeChooser.SelectedIndex = saved.Theme;
        Root.RequestedTheme = saved.Theme switch { 1 => ElementTheme.Light, 2 => ElementTheme.Dark, _ => ElementTheme.Default };
        SystemFrequencyChooser.SelectedIndex = Array.IndexOf(SystemIntervals, saved.SystemMilliseconds);
        ThermalFrequencyChooser.SelectedIndex = Array.IndexOf(ThermalIntervals, saved.ThermalMilliseconds);
        _timer.Interval = TimeSpan.FromMilliseconds(saved.SystemMilliseconds);
        _thermalTimer.Interval = TimeSpan.FromMilliseconds(saved.ThermalMilliseconds);
        Navigation.IsPaneOpen = saved.PaneOpen;
        SidebarBrand.Visibility = saved.PaneOpen ? Visibility.Visible : Visibility.Collapsed;
        var version = typeof(MainWindow).Assembly.GetName().Version;
        AboutVersion.Text = $"版本 {version?.Major}.{version?.Minor}.{version?.Build}";
        try { StartupToggle.IsOn = !IsSmokeRun && KClevoPanel.Core.StartupRegistration.IsEnabled(Environment.ProcessPath!); }
        catch (Exception error) { StartupDescription.Text = "无法读取启动项：" + error.Message; }
        _settingsReady = true;
        UpdateSamplingSummary();
    }
    private bool _startupUpdating;
    private void StartupToggled(object sender, RoutedEventArgs e)
    {
        if (!_settingsReady || _startupUpdating || IsSmokeRun) return;
        try
        {
            KClevoPanel.Core.StartupRegistration.SetEnabled(Environment.ProcessPath!, StartupToggle.IsOn);
            StartupDescription.Text = StartupToggle.IsOn ? "已开启：登录 Windows 后在托盘运行。移动程序后请重新开启。" : "已关闭开机自启动。";
        }
        catch (Exception error)
        {
            _startupUpdating = true;
            try { StartupToggle.IsOn = KClevoPanel.Core.StartupRegistration.IsEnabled(Environment.ProcessPath!); } catch { StartupToggle.IsEnabled = false; }
            _startupUpdating = false; StartupDescription.Text = "启动项未保存：" + error.Message;
        }
    }
    private void SavePreferences(bool? paneOpen = null)
    {
        if (!_settingsReady || IsSmokeRun) return;
        try
        {
            var settings = new Preferences(ThemeChooser.SelectedIndex, (int)_timer.Interval.TotalMilliseconds,
                (int)_thermalTimer.Interval.TotalMilliseconds, paneOpen ?? Navigation.IsPaneOpen).Normalize();
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencesPath)!);
            string temporary = PreferencesPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings));
            File.Move(temporary, PreferencesPath, overwrite: true);
        }
        catch { SamplingSummary.Text = "设置已在本次运行生效，但暂时无法保存到本机。"; }
    }
    private void FrequencyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsReady) return;
        if (SystemFrequencyChooser.SelectedIndex is >= 0 and < 4)
            _timer.Interval = TimeSpan.FromMilliseconds(SystemIntervals[SystemFrequencyChooser.SelectedIndex]);
        if (ThermalFrequencyChooser.SelectedIndex is >= 0 and < 3)
            _thermalTimer.Interval = TimeSpan.FromMilliseconds(ThermalIntervals[ThermalFrequencyChooser.SelectedIndex]);
        UpdateSamplingSummary(); UpdatePageStatus(); SavePreferences();
    }
    private void UpdateSamplingSummary()
    {
        SamplingSummary.Text = $"当前：{SamplingDescription}。更改立即生效；采样较慢时曲线仍平滑滚动。";
    }
    private void PaneOpening(NavigationView sender, object args)
    { if (SidebarBrand is not null) SidebarBrand.Visibility = Visibility.Visible; SavePreferences(true); }
    private void PaneClosing(NavigationView sender, object args)
    { if (SidebarBrand is not null) SidebarBrand.Visibility = Visibility.Collapsed; SavePreferences(false); }
    private void ChannelsClicked(object sender, RoutedEventArgs e) => ShowPage("channels");
    private void AboutClicked(object sender, RoutedEventArgs e) => ShowPage("about");
    private void SettingsRowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid row || row.Children.LastOrDefault() is not Grid control) return;
        bool narrow = e.NewSize.Width < 560;
        Grid.SetRow(control, narrow ? 1 : 0); Grid.SetColumn(control, narrow ? 1 : 2);
        Grid.SetColumnSpan(control, narrow ? 2 : 1);
        control.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
    }
    private void AboutFeaturesSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < 560;
        var second = (FrameworkElement)AboutFeatures.Children[1];
        Grid.SetRow(second, narrow ? 1 : 0); Grid.SetColumn(second, narrow ? 0 : 1);
        AboutFeatures.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
    }
    private void BackToSettingsClicked(object sender, RoutedEventArgs e) => ShowPage("settings");
    private Microsoft.UI.Xaml.Media.Animation.Storyboard? _pageAnimation;
    private void PageHeaderSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < 700;
        Grid.SetRow(HeaderActions, narrow ? 1 : 0); Grid.SetColumn(HeaderActions, narrow ? 0 : 1);
        Grid.SetColumnSpan(HeaderActions, narrow ? 3 : 1);
        HeaderActions.HorizontalAlignment = HorizontalAlignment.Right;
    }
    private void AnimatePage()
    {
        _pageAnimation?.Stop();
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        var animation = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(200), FillBehavior = Microsoft.UI.Xaml.Media.Animation.FillBehavior.Stop };
        var move = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { From = 12, To = 0, Duration = TimeSpan.FromMilliseconds(220), EnableDependentAnimation = true, FillBehavior = Microsoft.UI.Xaml.Media.Animation.FillBehavior.Stop,
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut } };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, PageContent); Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(move, PageContent.RenderTransform); Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(move, "Y");
        animation.Children.Add(fade); animation.Children.Add(move); _pageAnimation = animation; animation.Begin();
    }
    private void ShowPage(string page)
    {
        bool changed = _page != page;
        _page = page;
        OverviewPanel.Visibility = page == "overview" ? Visibility.Visible : Visibility.Collapsed;
        DevicePanel.Visibility = page == "device" ? Visibility.Visible : Visibility.Collapsed;
        FanPanel.Visibility = page == "fans" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        ChannelsPanel.Visibility = page == "channels" ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = page == "about" ? Visibility.Visible : Visibility.Collapsed;
        HeaderActions.Visibility = page == "overview" ? Visibility.Visible : Visibility.Collapsed;
        MainPresets.Visibility = page == "overview" ? Visibility.Visible : Visibility.Collapsed;
        PresetStatus.Visibility = page == "overview" ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.Visibility = page is "overview" or "device" or "fans" ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.IsEnabled = page != "fans" || Fans.CanRefresh;
        ErrorBar.Visibility = page == "overview" ? Visibility.Visible : Visibility.Collapsed;
        ContentScroll.ChangeView(null, 0, null, true);
        if (page == "device") _ = HardwareDetails.EnsureLoadedAsync();
        if (page == "fans") _ = Fans.EnsureLoadedAsync();
        PageTitle.Text = page switch { "channels" => "读取通道", "fans" => "散热控制", "device" => "设备信息", "settings" => "设置", "about" => "关于", _ => "性能概览" };
        UpdatePageStatus();
        if (changed) AnimatePage();
    }
}
