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
        _settingsReady = true;
        UpdateSamplingSummary();
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
    private void AboutClicked(object sender, RoutedEventArgs e) => ShowPage("about");
    private void BackToSettingsClicked(object sender, RoutedEventArgs e) => ShowPage("settings");
    private void ShowPage(string page)
    {
        _page = page;
        OverviewPanel.Visibility = page == "overview" ? Visibility.Visible : Visibility.Collapsed;
        DevicePanel.Visibility = page == "device" ? Visibility.Visible : Visibility.Collapsed;
        FanPanel.Visibility = page == "fans" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = page == "about" ? Visibility.Visible : Visibility.Collapsed;
        HeaderActions.Visibility = page is "overview" or "device" ? Visibility.Visible : Visibility.Collapsed;
        MainPresets.Visibility = page == "overview" ? Visibility.Visible : Visibility.Collapsed;
        PresetStatus.Visibility = page == "overview" ? Visibility.Visible : Visibility.Collapsed;
        RefreshText.Visibility = page == "overview" ? Visibility.Collapsed : Visibility.Visible;
        ErrorBar.Visibility = page == "overview" ? Visibility.Visible : Visibility.Collapsed;
        ContentScroll.ChangeView(null, 0, null, true);
        if (page == "device") _ = HardwareDetails.EnsureLoadedAsync();
        if (page == "fans") _ = Fans.EnsureLoadedAsync();
        PageTitle.Text = page switch { "fans" => "散热控制", "device" => "设备信息", "settings" => "设置", "about" => "关于", _ => "性能概览" };
        UpdatePageStatus();
    }
}
