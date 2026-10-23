using System.Text;
using System.Text.Json;
using KClevoPanel.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace KClevoPanel.App;

public sealed partial class HardwarePage : UserControl
{
    private static readonly string[] Order = ["cpu", "gpu", "memory", "storage", "board", "network", "audio", "system"];
    private readonly Dictionary<string, HardwareCategory> _categories = [];
    private readonly Dictionary<string, Border> _cards = [];
    private readonly Dictionary<string, Expander> _details = [];
    private readonly StackPanel _leftColumn = new() { Spacing = 16 };
    private readonly StackPanel _rightColumn = new() { Spacing = 16 };
    private CancellationTokenSource? _load;
    private Task? _readTask;
    private bool _reading, _closed;
    private string _bridgeSummary = "";
    internal Func<object?>? CurrentReadings { get; set; }
    public HardwareReport? Report { get; private set; }
    internal FrameworkElement RenderRoot => PageRoot;
    public HardwarePage()
    {
        InitializeComponent();
        HardwareError.RegisterPropertyChangedCallback(InfoBar.IsOpenProperty, (_, _) =>
            HardwareError.Visibility = HardwareError.IsOpen ? Visibility.Visible : Visibility.Collapsed);
    }
    public void SetMachine(MachineInfo machine)
    {
        if (machine.Model != "不可用") DeviceModel.Text = machine.Model;
        DeviceBrand.Text = machine.Manufacturer == "不可用" ? "设备档案" : machine.Manufacturer + " · 设备档案";
        BridgeDrivers.Children.Clear();
        foreach (var driver in machine.Drivers) BridgeDrivers.Children.Add(Secondary($"{driver.Name} · {driver.Version} · {driver.Inf}"));
        BridgeSection.Visibility = machine.Drivers.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _bridgeSummary = string.Join("\n", machine.Drivers.Select(d => $"{d.Name} · {d.Version} · {d.Inf}"));
    }
    public Task EnsureLoadedAsync() => Report is null ? RefreshAsync() : Task.CompletedTask;
    public Task RefreshAsync()
    {
        if (_closed) return Task.CompletedTask;
        if (_reading) return _readTask ?? Task.CompletedTask;
        return _readTask = LoadAsync();
    }
    private async Task LoadAsync()
    {
        if (_reading || _closed) return;
        _reading = true; LoadingRing.IsActive = true; LoadingRing.Visibility = Visibility.Visible;
        HardwareError.IsOpen = false; ReadStatus.Text = "正在读取硬件…";
        var cancellation = _load = new CancellationTokenSource();
        var completed = new HashSet<string>();
        try
        {
            var progress = new Progress<HardwareCategory>(category =>
            {
                if (_closed || cancellation.IsCancellationRequested || !ReferenceEquals(_load, cancellation)) return;
                ApplyCategory(category);
                completed.Add(category.Key);
                ReadStatus.Text = $"已读取 {completed.Count} / {Order.Length} 类";
            });
            var report = await HardwareInventory.ReadAsync(progress, cancellation.Token);
            if (_closed || cancellation.IsCancellationRequested) return;
            // Apply once more to make completion independent of queued progress callbacks.
            foreach (var category in report.Categories) ApplyCategory(category);
            Report = report;
            ReadStatus.Text = $"更新于 {report.CapturedAt:HH:mm:ss}";
            var errors = report.Categories.Where(c => c.Errors.Length > 0).Select(c => c.Title).ToArray();
            HardwareError.IsOpen = errors.Length > 0;
            HardwareError.Message = errors.Length > 0 ? string.Join("、", errors) + "读取失败，可点击页面顶部刷新重试。" : "";
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!_closed) { HardwareError.IsOpen = true; HardwareError.Message = e.Message; ReadStatus.Text = "读取未完成"; } }
        finally
        {
            _reading = false;
            if (!_closed) { LoadingRing.IsActive = false; LoadingRing.Visibility = Visibility.Collapsed; }
            if (ReferenceEquals(_load, cancellation)) _load = null;
            cancellation.Dispose();
        }
    }
    public void CancelRead() { _closed = true; _load?.Cancel(); }
    private Style S(string key) => (Style)PageRoot.Resources[key];
    private TextBlock Secondary(string text) => new() { Text = text, Style = S("SecondaryText") };
    private void ApplyCategory(HardwareCategory category)
    {
        _categories[category.Key] = category;
        _cards[category.Key] = BuildCard(category);
        LayoutCards();
        if (_categories.TryGetValue("cpu", out var cpu) && cpu.Devices.FirstOrDefault() is { } processor)
            CpuSummary.Text = processor.Properties.FirstOrDefault(p => p.Label == "核心 / 线程")?.Value ?? "—";
        if (_categories.TryGetValue("memory", out var memory))
        {
            MemorySummary.Text = HardwareInventory.BinarySize(SumCapacity(memory.Devices));
            MemoryCount.Text = $"{memory.Devices.Length} 条内存模块";
        }
        if (_categories.TryGetValue("storage", out var storage))
        {
            var local = storage.Devices.Where(d => !d.IsExternal && !d.IsVirtual).ToArray();
            StorageSummary.Text = HardwareInventory.DiskSize(SumCapacity(local));
            StorageCount.Text = $"{local.Length} 块本地 · {storage.Devices.Count(d => d.IsExternal)} 个外接";
        }
    }
    private static ulong? SumCapacity(IEnumerable<HardwareDevice> devices)
    {
        var capacities = devices.Where(d => d.CapacityBytes.HasValue).Select(d => d.CapacityBytes!.Value).ToArray();
        return capacities.Length == 0 ? null : capacities.Aggregate(0UL, (sum, value) => checked(sum + value));
    }
    private Border BuildCard(HardwareCategory category)
    {
        var body = new StackPanel { Spacing = 16 };
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new());
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(new FontIcon { Glyph = category.Glyph, Style = S("CategoryIcon") });
        var title = new TextBlock { Text = category.Title, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1); header.Children.Add(title);
        var count = Secondary(category.Devices.Length.ToString()); Grid.SetColumn(count, 2); count.VerticalAlignment = VerticalAlignment.Center; header.Children.Add(count);
        body.Children.Add(header);
        if (category.Devices.Length == 0) body.Children.Add(Secondary(category.Errors.Length > 0 ? "读取暂不可用" : "系统未报告此类设备"));
        var properties = new StackPanel { Spacing = 16 };
        foreach (var device in category.Devices)
        {
            var entry = new StackPanel { Spacing = 5 };
            entry.Children.Add(new TextBlock { Text = category.Key == "cpu" ? DeviceLabels.CpuModel(device.Name) : device.Name, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            entry.Children.Add(Secondary(device.Summary));
            if (device.IsExternal || device.IsVirtual)
                entry.Children.Add(new Border { Style = S("BadgeBorder"), HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new TextBlock { Text = device.IsVirtual ? "虚拟 / 软件设备" : "外接设备", FontSize = 11 } });
            body.Children.Add(entry);
            if (category.Key == "cpu")
            {
                var facts = new Grid { ColumnSpacing = 16 };
                facts.ColumnDefinitions.Add(new()); facts.ColumnDefinitions.Add(new());
                int factColumn = 0;
                foreach (var fact in device.Properties.Where(p => p.Label is "L3 缓存" or "插槽标识"))
                {
                    var factBlock = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 4) };
                    factBlock.Children.Add(Secondary(fact.Label));
                    factBlock.Children.Add(new TextBlock { Text = fact.Value, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                    Grid.SetColumn(factBlock, factColumn++); facts.Children.Add(factBlock);
                }
                body.Children.Add(facts);
            }
            if (device.Properties.Length == 0) continue;
            var detail = new StackPanel { Spacing = 8 };
            detail.Children.Add(new TextBlock { Text = device.Name, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            foreach (var property in device.Properties)
            {
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new() { Width = new GridLength(104) }); row.ColumnDefinitions.Add(new());
                row.Children.Add(new TextBlock { Text = property.Label, Style = S("FieldLabel") });
                var value = new TextBlock { Text = property.Value, Style = S("FieldValue") }; Grid.SetColumn(value, 1); row.Children.Add(value);
                detail.Children.Add(row);
            }
            properties.Children.Add(detail);
        }
        if (properties.Children.Count > 0)
        {
            var expander = new Expander { Header = "详细规格", Content = properties, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, IsExpanded = _details.TryGetValue(category.Key, out var old) && old.IsExpanded };
            _details[category.Key] = expander; body.Children.Add(expander);
        }
        return new Border { Style = S("HardwareCard"), Child = body, VerticalAlignment = VerticalAlignment.Top };
    }
    private void CardsSizeChanged(object sender, SizeChangedEventArgs e) { if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 1) LayoutCards(); }
    private void LayoutCards()
    {
        int columns = CardsHost.ActualWidth >= 680 ? 2 : 1;
        _leftColumn.Children.Clear(); _rightColumn.Children.Clear();
        CardsHost.Children.Clear(); CardsHost.RowDefinitions.Clear(); CardsHost.ColumnDefinitions.Clear();
        for (int c = 0; c < columns; c++) CardsHost.ColumnDefinitions.Add(new());
        CardsHost.Children.Add(_leftColumn);
        if (columns == 2) { Grid.SetColumn(_rightColumn, 1); CardsHost.Children.Add(_rightColumn); }
        for (int index = 0; index < Order.Length; index++)
        {
            if (!_cards.TryGetValue(Order[index], out var card)) continue;
            // Stable category columns, each measuring its own content. Expanding a card never opens a gap in its neighbour.
            (columns == 2 && index % 2 == 1 ? _rightColumn : _leftColumn).Children.Add(card);
        }
    }
    private string TextReport()
    {
        var text = new StringBuilder(DeviceModel.Text + "\n");
        foreach (string key in Order)
        {
            if (!_categories.TryGetValue(key, out var category)) continue;
            text.AppendLine("\n" + category.Title);
            foreach (var device in category.Devices)
            {
                text.AppendLine(device.Name + " · " + device.Summary);
                foreach (var property in device.Properties) text.AppendLine($"  {property.Label}：{property.Value}");
            }
        }
        if (_bridgeSummary.Length > 0) text.AppendLine("\nACPI 桥驱动\n" + _bridgeSummary);
        return text.ToString();
    }
    private void CopyClicked(object sender, RoutedEventArgs e)
    {
        try { var data = new DataPackage(); data.SetText(TextReport()); Clipboard.SetContent(data); ActionStatus.Text = "已复制硬件清单"; }
        catch (Exception error) { ActionStatus.Text = "复制失败：" + error.Message; }
    }
    private async void ExportClicked(object sender, RoutedEventArgs e)
    {
        if (Report is null) { ActionStatus.Text = "等待读取完成后即可导出。"; return; }
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KClevoPanel", "Exports");
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, $"hardware-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { Model = DeviceModel.Text, Hardware = Report, CurrentReadings = CurrentReadings?.Invoke() }, new JsonSerializerOptions { WriteIndented = true }));
            ActionStatus.Text = "已保存：" + file;
        }
        catch (Exception error) { ActionStatus.Text = "导出失败：" + error.Message; }
    }
    internal void ExpandForSmoke(string key) { if (_details.TryGetValue(key, out var detail)) detail.IsExpanded = true; }
    internal FrameworkElement CategoryForSmoke(string key) => _cards[key];
    internal int ColumnCountForSmoke => CardsHost.ColumnDefinitions.Count;
    internal bool CardsPackedForSmoke => Packed(_leftColumn) && (ColumnCountForSmoke == 1 || Packed(_rightColumn));
    private static bool Packed(StackPanel column)
    {
        FrameworkElement? previous = null;
        foreach (var card in column.Children.OfType<FrameworkElement>())
        {
            double top = card.TransformToVisual(column).TransformPoint(new()).Y;
            if (previous is not null)
            {
                double bottom = previous.TransformToVisual(column).TransformPoint(new()).Y + previous.ActualHeight;
                if (Math.Abs(top - bottom - column.Spacing) > 1) return false;
            }
            previous = card;
        }
        return true;
    }
}
