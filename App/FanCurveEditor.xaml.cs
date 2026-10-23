using KClevoPanel.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace KClevoPanel.App;

public sealed partial class FanCurveEditor : UserControl
{
    private FanCurve? _curve, _runtime;
    private bool _sync;
    private int _drag = -1;
    private Brush _color = new SolidColorBrush(Microsoft.UI.Colors.DeepSkyBlue);
    public event Action? Edited;
    public FanCurve? Curve => _curve;
    public FanCurveEditor() { InitializeComponent(); ActualThemeChanged += (_, _) => Draw(); }
    public void Configure(string name, Brush color) { FanTitle.Text = name; _color = color; LegendColor.Background = color; Draw(); }
    public void LoadCurve(FanCurve curve)
    {
        _curve = new(curve.Nodes.ToArray());
        SetBoxes(); Draw();
    }
    public void SetRuntime(FanCurve? curve, FanReading? reading, double? temperature)
    {
        _runtime = curve;
        TelemetryLabel.Text = $"{DeviceLabels.Temperature(temperature)} · {(reading?.Rpm is { } rpm ? rpm + " RPM" : "— RPM")}";
        Draw();
    }
    private void SetBoxes()
    {
        if (_curve is null) return;
        _sync = true;
        T2.Value = _curve.Nodes[1].Temperature; D2.Value = _curve.Nodes[1].Percent;
        T3.Value = _curve.Nodes[2].Temperature; D3.Value = _curve.Nodes[2].Percent;
        EndpointLabel.Text = $"固件固定：{_curve.Nodes[0].Temperature}°C / {_curve.Nodes[0].Percent}% → {_curve.Nodes[3].Temperature}°C / 100%";
        _sync = false;
    }
    private void NodeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_sync || _curve is null) return;
        if (!double.IsFinite(args.NewValue)) { SetBoxes(); return; }
        int node = sender == T2 || sender == D2 ? 1 : 2;
        var old = _curve.Nodes[node];
        EditNode(node, sender == T2 || sender == T3 ? (int)Math.Round(args.NewValue) : old.Temperature,
            sender == D2 || sender == D3 ? (int)Math.Round(args.NewValue) : old.Percent);
    }
    internal void EditNode(int node, int temperature, int duty)
    {
        if (_curve is null || node is not (1 or 2)) return;
        var points = _curve.Nodes.ToArray();
        int minTemp = points[node - 1].Temperature + 1, maxTemp = Math.Min(node == 1 ? 90 : 95, points[node + 1].Temperature - 1);
        int minDuty = Math.Max(30, (int)Math.Ceiling(points[node - 1].RawDuty * 100d / 255)), maxDuty = points[node + 1].Percent;
        if (maxTemp < minTemp || maxDuty < minDuty) return;
        points[node] = FanNode.FromPercent(Math.Clamp(temperature, minTemp, maxTemp), Math.Clamp(duty, minDuty, maxDuty));
        _curve = new(points); SetBoxes(); Draw(); Edited?.Invoke();
    }
    private Point Position(FanNode node) => new(28 + (Plot.ActualWidth - 42) * (node.Temperature - 20) / 80d, 18 + (Plot.ActualHeight - 46) * (1 - node.RawDuty / 255d));
    private void PlotChanged(object sender, SizeChangedEventArgs e) => Draw();
    private void Draw()
    {
        if (Plot is null || Plot.ActualWidth < 60 || Plot.ActualHeight < 60) return;
        Plot.Children.Clear();
        Brush grid = GridStyle.Background;
        Brush text = EndpointLabel.Foreground;
        for (int t = 20; t <= 100; t += 20)
        {
            double x = Position(new(t, 0)).X;
            Plot.Children.Add(new Line { X1 = x, X2 = x, Y1 = 18, Y2 = Plot.ActualHeight - 28, Stroke = grid, StrokeThickness = 1 });
            var label = new TextBlock { Text = t.ToString(), FontSize = 10, Foreground = text }; Canvas.SetLeft(label, x - 7); Canvas.SetTop(label, Plot.ActualHeight - 22); Plot.Children.Add(label);
        }
        for (int d = 0; d <= 100; d += 25)
        {
            double y = 18 + (Plot.ActualHeight - 46) * (1 - d / 100d);
            Plot.Children.Add(new Line { X1 = 28, X2 = Plot.ActualWidth - 14, Y1 = y, Y2 = y, Stroke = grid, StrokeThickness = 1 });
            var label = new TextBlock { Text = d.ToString(), FontSize = 10, Foreground = text }; Canvas.SetLeft(label, 0); Canvas.SetTop(label, y - 7); Plot.Children.Add(label);
        }
        if (_runtime is not null) AddLine(_runtime, text, true);
        if (_curve is null) return;
        var area = new Polygon { Fill = _color, Opacity = 0.08, IsHitTestVisible = false };
        area.Points.Add(Position(_curve.Nodes[0]) with { Y = Plot.ActualHeight - 28 });
        foreach (var n in _curve.Nodes) area.Points.Add(Position(n));
        area.Points.Add(Position(_curve.Nodes[3]) with { Y = Plot.ActualHeight - 28 }); Plot.Children.Add(area);
        AddLine(_curve, _color, false);
        for (int i = 0; i < 4; i++)
        {
            var p = Position(_curve.Nodes[i]); int size = i is 1 or 2 ? 12 : 6;
            var dot = new Ellipse { Width = size, Height = size, Fill = _color, Stroke = EditorCard.Background, StrokeThickness = 2 };
            ToolTipService.SetToolTip(dot, $"节点 {i + 1}：{_curve.Nodes[i].Temperature}°C / {_curve.Nodes[i].Percent}%" + (i is 1 or 2 ? " · 可拖动" : " · 固件固定"));
            Canvas.SetLeft(dot, p.X - size / 2d); Canvas.SetTop(dot, p.Y - size / 2d); Plot.Children.Add(dot);
        }
    }
    private void AddLine(FanCurve curve, Brush color, bool dashed)
    {
        var line = new Polyline { Stroke = color, StrokeThickness = dashed ? 1.5 : 2, IsHitTestVisible = false };
        if (dashed) line.StrokeDashArray = new DoubleCollection { 4, 4 };
        foreach (var n in curve.Nodes) line.Points.Add(Position(n)); Plot.Children.Add(line);
    }
    private void PlotPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_curve is null || !IsEnabled) return;
        var p = e.GetCurrentPoint(Plot).Position;
        _drag = Enumerable.Range(1, 2).FirstOrDefault(i => Math.Abs(Position(_curve.Nodes[i]).X - p.X) < 20 && Math.Abs(Position(_curve.Nodes[i]).Y - p.Y) < 20, -1);
        if (_drag >= 0) { Plot.CapturePointer(e.Pointer); e.Handled = true; }
    }
    private void PlotMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag < 0) return;
        var p = e.GetCurrentPoint(Plot).Position;
        EditNode(_drag, (int)Math.Round(20 + (p.X - 28) / (Plot.ActualWidth - 42) * 80), (int)Math.Round((1 - (p.Y - 18) / (Plot.ActualHeight - 46)) * 100));
        e.Handled = true;
    }
    private void PlotReleased(object sender, PointerRoutedEventArgs e) { _drag = -1; Plot.ReleasePointerCaptures(); }
    private void PlotCaptureLost(object sender, PointerRoutedEventArgs e) => _drag = -1;
}
