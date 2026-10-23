using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using KClevoPanel.Core;
using System.Diagnostics;

namespace KClevoPanel.App;
public sealed partial class HistoryChart : UserControl
{
    private SeriesHistory? _history;
    private DateTimeOffset _now;
    private double _maximum = 100;
    private bool _active = true, _paused, _loaded;
    private readonly DispatcherTimer _animationTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    internal long FrameCount { get; private set; }
    internal bool FullWindowEdgesCovered => _segments.Any(s => s.Line.Points.Count > 0 && Math.Abs(s.Line.Points[0].X - 2.5) < 0.01)
        && _segments.Any(s => s.Line.Points.Count > 0 && Math.Abs(s.Line.Points[^1].X - (Plot.ActualWidth - 2.5)) < 0.01)
        && _dot.Visibility == Visibility.Visible && Canvas.GetLeft(_dot) >= 0
        && Canvas.GetLeft(_dot) + _dot.Width <= Plot.ActualWidth;
    private long _lastFrame;
    private DateTimeOffset _arrival;
    private DateTimeOffset? _latestTime;
    private double? _from, _target;
    private readonly Polygon _fill = new() { Opacity = 0.11 };
    private readonly Polyline _line = new() { StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round };
    private readonly Ellipse _dot = new() { Width = 3, Height = 3 };
    private readonly List<(Polygon Fill, Polyline Line)> _segments = [];
    private double _gridWidth, _gridHeight;
    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(nameof(LineBrush), typeof(Brush), typeof(HistoryChart),
        new PropertyMetadata(new SolidColorBrush(Windows.UI.Color.FromArgb(255, 109, 174, 255)), (o, _) => ((HistoryChart)o).Render()));
    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public HistoryChart()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => { _gridWidth = -1; Render(); };
        _segments.Add((_fill, _line));
        _animationTimer.Tick += (_, _) => Frame(null, EventArgs.Empty);
        Loaded += (_, _) => { _loaded = true; SetAnimationState(); };
        Unloaded += (_, _) => { _loaded = false; _animationTimer.Stop(); };
    }
    private void SetAnimationState() { if (_loaded && _active && !_paused) _animationTimer.Start(); else _animationTimer.Stop(); }
    public void SetActive(bool active) { _active = active; SetAnimationState(); if (active) Render(); }
    public void SetPaused(bool paused) { _paused = paused; SetAnimationState(); Render(); }
    public void Update(SeriesHistory history, DateTimeOffset now, double maximum = 100)
    {
        var latest = history.Points.LastOrDefault();
        if (!ReferenceEquals(_history, history) || latest?.Time != _latestTime || latest?.Value != _target)
        {
            // Cached EC samples arrive twice on the faster system clock; don't restart their animation.
            _from = _target; _target = latest?.Value; _latestTime = latest?.Time;
            _arrival = DateTimeOffset.Now;
        }
        _history = history; _now = now; _maximum = maximum;
        if (_active) Render();
    }
    private void Frame(object? sender, object e)
    {
        if (!_active || _paused || Stopwatch.GetElapsedTime(_lastFrame).TotalMilliseconds < 12) return;
        _lastFrame = Stopwatch.GetTimestamp(); FrameCount++; Render();
    }
    private void PlotSizeChanged(object sender, SizeChangedEventArgs e) => Render();
    private void Render()
    {
        if (Plot is null || Plot.ActualWidth <= 0 || Plot.ActualHeight <= 0) return;
        double width = Plot.ActualWidth, height = Plot.ActualHeight;
        // Leave room for the whole endpoint marker and center the outer grid strokes inside the canvas.
        const double left = 2.5, top = 2.5;
        double right = width - 2.5, bottom = height - 2.5;
        double X(double seconds) => left + (right - left) * (1 - seconds / 60);
        double Y(double value) => top + (bottom - top) * (1 - Math.Clamp(value, 0, _maximum) / _maximum);
        if (_gridWidth != width || _gridHeight != height)
        {
            _gridWidth = width; _gridHeight = height; Plot.Children.Clear();
            Plot.Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, height) };
            var dictionary = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries[
                ActualTheme == ElementTheme.Light ? "Light" : "Default"];
            var gridBrush = (Brush)dictionary["GraphGridBrush"];
            for (int i = 0; i <= 6; i++) Plot.Children.Add(new Line { X1 = left + i * (right - left) / 6, X2 = left + i * (right - left) / 6, Y1 = top, Y2 = bottom, Stroke = gridBrush, StrokeThickness = 1 });
            for (int i = 0; i <= 4; i++) Plot.Children.Add(new Line { X1 = left, X2 = right, Y1 = top + i * (bottom - top) / 4, Y2 = top + i * (bottom - top) / 4, Stroke = gridBrush, StrokeThickness = 1 });
            foreach (var shapes in _segments) { Plot.Children.Add(shapes.Fill); Plot.Children.Add(shapes.Line); }
            Plot.Children.Add(_dot);
        }
        foreach (var shapes in _segments)
        {
            shapes.Fill.Points.Clear(); shapes.Line.Points.Clear();
            shapes.Fill.Visibility = Visibility.Collapsed; shapes.Line.Visibility = Visibility.Collapsed;
        }
        _dot.Fill = LineBrush;
        if (_history is null) { _dot.Visibility = Visibility.Collapsed; return; }
        // A null point or a long interval breaks a segment. Retained shapes avoid rebuilding the grid every frame.
        var segment = new List<Point>();
        DateTimeOffset displayTime = _paused ? _now : DateTimeOffset.Now;
        int segmentIndex = 0;
        SeriesPoint? previous = null;
        var latest = _history.Points.LastOrDefault();
        double AnimatedValue(double value) => _from.HasValue && _target.HasValue && !_paused
            ? _from.Value + (_target.Value - _from.Value) * Math.Clamp((displayTime - _arrival).TotalMilliseconds / 250, 0, 1) : value;
        void Finish()
        {
            if (segment.Count == 0) return;
            if (segmentIndex >= _segments.Count)
            {
                var fill = new Polygon { Opacity = 0.11 };
                var line = new Polyline { StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round };
                _segments.Add((fill, line)); Plot.Children.Add(fill); Plot.Children.Add(line);
            }
            var shapes = _segments[segmentIndex++];
            shapes.Fill.Fill = LineBrush; shapes.Line.Stroke = LineBrush;
            shapes.Fill.Visibility = Visibility.Visible; shapes.Line.Visibility = Visibility.Visible;
            shapes.Fill.Points.Add(new(segment[0].X, bottom));
            foreach (var point in segment) shapes.Fill.Points.Add(point);
            shapes.Fill.Points.Add(new(segment[^1].X, bottom));
            foreach (var point in segment) shapes.Line.Points.Add(point);
            segment.Clear();
        }
        foreach (var point in _history.Points)
        {
            double seconds = (displayTime - point.Time).TotalSeconds;
            if (seconds < 0) continue;
            if (seconds > 60) { previous = point; continue; }
            bool continuous = previous?.Value is not null && point.Time - previous.Time <= TimeSpan.FromSeconds(6);
            if (point.Value is null || !continuous) Finish();
            if (point.Value is null) { previous = point; continue; }
            double sampleValue = point.Value.Value;
            if (point == latest) sampleValue = AnimatedValue(sampleValue);
            if (continuous && previous!.Time < displayTime.AddSeconds(-60))
            {
                double fraction = (displayTime.AddSeconds(-60) - previous.Time).TotalSeconds / (point.Time - previous.Time).TotalSeconds;
                double boundaryValue = previous.Value!.Value + (sampleValue - previous.Value.Value) * fraction;
                segment.Add(new(left, Y(boundaryValue)));
            }
            segment.Add(new(X(seconds), Y(sampleValue)));
            previous = point;
        }
        // Hold a fresh last reading to "now" for display only. Stale readings and missing samples remain gaps.
        double age = latest is null ? double.PositiveInfinity : (displayTime - latest.Time).TotalSeconds;
        bool fresh = latest?.Value is not null && age >= 0 && age <= 2;
        if (fresh && segment.Count > 0) segment.Add(new(right, Y(AnimatedValue(latest!.Value!.Value))));
        Finish();
        if (fresh && latest?.Value is { } value)
        {
            Canvas.SetLeft(_dot, right - _dot.Width / 2);
            Canvas.SetTop(_dot, Y(AnimatedValue(value)) - _dot.Height / 2);
            _dot.Visibility = Visibility.Visible;
        }
        else _dot.Visibility = Visibility.Collapsed;
    }
}
