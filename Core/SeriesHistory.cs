namespace KClevoPanel.Core;

public sealed record SeriesPoint(DateTimeOffset Time, double? Value);
public sealed class SeriesHistory
{
    private readonly List<SeriesPoint> _points = [];
    public IReadOnlyList<SeriesPoint> Points => _points;
    public void Add(DateTimeOffset time, double? value)
    {
        if (value.HasValue && !double.IsFinite(value.Value)) value = null;
        if (_points.Count > 0 && time < _points[^1].Time) _points.Clear();
        if (_points.Count > 0 && time == _points[^1].Time) _points.RemoveAt(_points.Count - 1);
        _points.Add(new(time, value));
        // Keep one point before the visible window to clip a crossing segment precisely.
        var cutoff = time - TimeSpan.FromSeconds(60);
        int remove = 0;
        while (remove + 1 < _points.Count && _points[remove + 1].Time < cutoff) remove++;
        if (remove > 0) _points.RemoveRange(0, remove);
        if (_points.Count > 256) _points.RemoveRange(0, _points.Count - 256);
    }
}
