using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace KClevoPanel.App;
public sealed partial class PresetSelector : UserControl
{
    private int? _mode;
    private Storyboard? _animation;
    public event Action<int>? PresetRequested;
    public PresetSelector() { InitializeComponent(); IsEnabled = false; }
    public void SetState(int? mode, bool enabled)
    {
        bool changed = _mode != mode;
        _mode = mode is >= 0 and <= 3 ? mode : null; IsEnabled = enabled;
        PositionIndicator(changed);
        foreach (var button in new[] { Quiet, Saving, Performance, Entertainment })
        {
            bool selected = _mode == int.Parse((string)button.Tag);
            button.FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(button, selected ? "当前固件档位记录" : "切换固件性能档位");
        }
    }
    private void PositionIndicator(bool animate)
    {
        double from = Slide.X;
        _animation?.Stop();
        Indicator.Visibility = _mode.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (!_mode.HasValue || Track.ActualWidth <= 0) return;
        double width = Math.Max(0, (Track.ActualWidth - 18) / 4), x = _mode.Value * (width + 6);
        Indicator.Width = width;
        Slide.X = x;
        if (!animate) return;
        var motion = new DoubleAnimation { From = from, To = x, Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, EnableDependentAnimation = true };
        Storyboard.SetTarget(motion, Slide); Storyboard.SetTargetProperty(motion, "X");
        _animation = new Storyboard(); _animation.Children.Add(motion); _animation.Begin();
    }
    internal bool LayoutVerifiedForSmoke
    {
        get
        {
            var buttons = new[] { Quiet, Saving, Performance, Entertainment };
            for (int i = 1; i < buttons.Length; i++)
            {
                double left = buttons[i].TransformToVisual(Track).TransformPoint(new()).X;
                double previous = buttons[i-1].TransformToVisual(Track).TransformPoint(new()).X + buttons[i-1].ActualWidth;
                if (Math.Abs(left - previous - 6) > 0.5) return false;
            }
            return buttons.All(b => b.ActualWidth >= 60) && (!_mode.HasValue || Math.Abs(Indicator.Width - buttons[0].ActualWidth) < 0.5);
        }
    }
    private void TrackSizeChanged(object sender, SizeChangedEventArgs e) => PositionIndicator(false);
    private void SegmentClicked(object sender, RoutedEventArgs e)
    { if (sender is Button button && int.TryParse(button.Tag?.ToString(), out int mode) && mode != _mode) PresetRequested?.Invoke(mode); }
}
