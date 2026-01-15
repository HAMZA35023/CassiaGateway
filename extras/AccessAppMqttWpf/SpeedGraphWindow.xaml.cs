using AccessAppMqttWpf.Models;
using AccessAppMqttWpf.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AccessAppMqttWpf;

public partial class SpeedGraphWindow : Window
{
    private MainViewModel? Vm => DataContext as MainViewModel;

    // Keep history for up to 12 hours.
    private static readonly TimeSpan MaxHistory = TimeSpan.FromHours(12);

    // Default visible window
    private TimeSpan _visibleWindow = TimeSpan.FromHours(1);

    // Custom time range (set by drag-select on the chart).
    private (DateTimeOffset Min, DateTimeOffset Max)? _customRangeUtc;

    // Selection overlay state
    private bool _isSelecting;
    private Point _selectStart;
    private Rectangle? _selectRect;

    // Last plot geometry + time mapping (set by Redraw)
    private double _plotLeft, _plotTop, _plotW, _plotH;
    private DateTimeOffset _plotMinT, _plotMaxT;

    // ---- Gateway selector options (window-owned) ----
    public sealed class GatewayOption
    {
        public string Name { get; }
        public GatewayMode Mode { get; }
        public CassiaGateway? Gateway { get; }

        public GatewayOption(string name, GatewayMode mode, CassiaGateway? gateway = null)
        {
            Name = name;
            Mode = mode;
            Gateway = gateway;
        }

        public override string ToString() => Name;
    }

    public enum GatewayMode
    {
        Total, // one line: sum
        All,   // one line per gateway
        Single // one gateway
    }

    public List<GatewayOption> GatewayOptions { get; private set; } = new();

    // Bound from XAML via RelativeSource (Window)
    public GatewayOption? SelectedGatewayOption { get; set; }

    public SpeedGraphWindow(MainViewModel vm) : this() => DataContext = vm;
    public SpeedGraphWindow(MainWindow owner) : this()
    {
        Owner = owner;
        if (owner.DataContext is MainViewModel vm)
            DataContext = vm;
    }

    public SpeedGraphWindow()
    {
        InitializeComponent();

        Loaded += (_, _) => Hook();
        Unloaded += (_, _) => Unhook();

        SizeChanged += (_, _) => Redraw();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { ResetZoom(); e.Handled = true; }
            else if (e.Key == Key.F5) { Redraw(); e.Handled = true; }
        };
    }

    private void Hook()
    {
        if (Vm == null) return;

        Vm.PropertyChanged += Vm_PropertyChanged;
        HookHistory();

        BuildGatewayOptions();

        Redraw();
    }

    private void Unhook()
    {
        if (Vm == null) return;

        Vm.PropertyChanged -= Vm_PropertyChanged;
        UnhookHistory();
    }

    private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // If your VM replaces CassiaGateways list at runtime (rare), you can rebuild options here.
        // For typical use, speed history updates are enough.
    }

    private void BuildGatewayOptions()
    {
        if (Vm == null) return;

        GatewayOptions = new List<GatewayOption>
        {
            new GatewayOption("(Total)", GatewayMode.Total),
            new GatewayOption("(All gateways)", GatewayMode.All)
        };

        foreach (var gw in Vm.CassiaGateways.OrderBy(g => g.Name))
            GatewayOptions.Add(new GatewayOption(gw.Name, GatewayMode.Single, gw));

        GatewayCombo.ItemsSource = GatewayOptions;

        // Keep current selection if possible
        if (SelectedGatewayOption != null)
        {
            var match = GatewayOptions.FirstOrDefault(o =>
                o.Mode == SelectedGatewayOption.Mode &&
                ((o.Gateway == null && SelectedGatewayOption.Gateway == null) ||
                 (o.Gateway != null && SelectedGatewayOption.Gateway != null && o.Gateway.Name == SelectedGatewayOption.Gateway.Name)));

            SelectedGatewayOption = match ?? GatewayOptions.FirstOrDefault();
        }
        else
        {
            SelectedGatewayOption = GatewayOptions.FirstOrDefault(); // default: Total
        }

        GatewayCombo.SelectedItem = SelectedGatewayOption;
        UpdateGraphTitle();
    }

    private void UpdateGraphTitle()
    {
        var name = SelectedGatewayOption?.Name ?? "—";
        GraphTitleText.Text = $"Speed • {name}";
    }

    private void HookHistory()
    {
        if (Vm == null) return;

        foreach (var g in Vm.CassiaGateways)
            g.SpeedHistory.CollectionChanged += SpeedHistory_CollectionChanged;
    }

    private void UnhookHistory()
    {
        if (Vm == null) return;

        foreach (var g in Vm.CassiaGateways)
            g.SpeedHistory.CollectionChanged -= SpeedHistory_CollectionChanged;
    }

    private void SpeedHistory_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Redraw();

    private sealed record Series(string Name, IReadOnlyList<SpeedSample> Samples, Brush Stroke);

    private void GatewayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GatewayCombo.SelectedItem is GatewayOption opt)
        {
            SelectedGatewayOption = opt;
            UpdateGraphTitle();
            Redraw();
        }
    }

    private void Redraw()
    {
        if (Vm == null) return;
        if (ChartCanvas == null || OverlayCanvas == null) return;

        ChartCanvas.Children.Clear();
        OverlayCanvas.Children.Clear();

        PruneAllHistories();

        var opt = SelectedGatewayOption;
        if (opt == null)
        {
            ChartCanvas.Tag = "";
            Avg15Text.Text = "—";
            Avg30Text.Text = "—";
            RangeText.Text = "—";
            GraphTitleText.Text = "Speed";
            return;
        }

        // ---- Build series ----
        var series = BuildSeries(opt);
        if (series.Count == 0)
        {
            ChartCanvas.Tag = $"{opt.Name}: no speed history yet (need at least 2 samples)";
            Avg15Text.Text = "—";
            Avg30Text.Text = "—";
            RangeText.Text = "—";
            return;
        }

        // ---- Determine time range ----
        var maxT = series.SelectMany(s => s.Samples).Max(s => s.TimeUtc);

        // Apply preset window OR custom range
        var minT = maxT - _visibleWindow;

        if (_customRangeUtc is { } cr)
        {
            // Clamp custom range to available data and to MaxHistory
            var clampMin = maxT - MaxHistory;
            var cMin = cr.Min < clampMin ? clampMin : cr.Min;
            var cMax = cr.Max > maxT ? maxT : cr.Max;

            if (cMax > cMin)
            {
                minT = cMin;
                maxT = cMax;
            }
        }

        // Never show more than MaxHistory
        var minHard = maxT - MaxHistory;
        if (minT < minHard) minT = minHard;

        // Trim series to range
        series = series
            .Select(s => s with { Samples = s.Samples.Where(x => x.TimeUtc >= minT && x.TimeUtc <= maxT).ToList() })
            .Where(s => s.Samples.Count >= 2)
            .ToList();

        if (series.Count == 0)
        {
            ChartCanvas.Tag = $"{opt.Name}: no speed history in the selected range";
            Avg15Text.Text = "—";
            Avg30Text.Text = "—";
            RangeText.Text = "—";
            return;
        }

        // ---- Y scale ----
        var yMaxRaw = series.SelectMany(s => s.Samples).Max(s => s.SpeedPctPerMin);
        var yMax = NiceCeiling(Math.Max(1, yMaxRaw));

        // ---- Layout ----
        double left = 62, right = 18, top = 18, bottom = 40;
        var cw = Math.Max(0, ChartCanvas.ActualWidth);
        var ch = Math.Max(0, ChartCanvas.ActualHeight);

        var w = Math.Max(1, cw - left - right);
        var h = Math.Max(1, ch - top - bottom);

        var dt = Math.Max(1, (maxT - minT).TotalSeconds);

        // Store for selection mapping
        _plotLeft = left; _plotTop = top; _plotW = w; _plotH = h;
        _plotMinT = minT; _plotMaxT = maxT;

        // ---- Background + axes ----
        DrawPlotBackground(left, top, w, h);
        DrawAxes(left, top, w, h, minT, maxT, yMax);

        // ---- Series drawing ----
        foreach (var s in series)
            DrawSeriesPolyline(s, left, top, w, h, minT, dt, yMax);

        // ---- Legend on LEFT (inside plot) ----
        DrawLegend(series, left + 10, top + 10, maxT);

        // ---- Caption / stats ----
        var last = series.SelectMany(s => s.Samples).OrderBy(s => s.TimeUtc).Last();
        ChartCanvas.Tag = $"{opt.Name}: last {last.SpeedPctPerMin:0.##} %/min • range 0..{yMax:0} • series {series.Count}";

        // ---- Averages for last 15/30 minutes (aggregate across visible series) ----
        UpdateAverages(series, maxT);

        // ---- Visible range label ----
        RangeText.Text = $"{minT.ToLocalTime():HH:mm} – {maxT.ToLocalTime():HH:mm}";
        UpdateGraphTitle();
    }

    private List<Series> BuildSeries(GatewayOption opt)
    {
        if (Vm == null) return new();

        var brushes = new Brush[]
        {
            Brushes.Black, Brushes.Blue, Brushes.Red, Brushes.Green, Brushes.DarkOrange,
            Brushes.Purple, Brushes.Brown, Brushes.DarkCyan, Brushes.Gray
        };

        var series = new List<Series>();

        if (opt.Mode == GatewayMode.All)
        {
            int bi = 0;
            foreach (var gw in Vm.CassiaGateways.OrderBy(g => g.Name))
            {
                var s = gw.SpeedHistory.ToList();
                if (s.Count < 2) continue;
                series.Add(new Series(gw.Name, s, brushes[bi++ % brushes.Length]));
            }
            return series;
        }

        if (opt.Mode == GatewayMode.Total)
        {
            var nowUtc = Vm.CassiaGateways
                .SelectMany(g => g.SpeedHistory)
                .Select(s => s.TimeUtc)
                .DefaultIfEmpty(DateTimeOffset.UtcNow)
                .Max();

            var total = BuildTotalSeries(Vm.CassiaGateways, nowUtc, MaxHistory);

            if (total.Count >= 2)
                series.Add(new Series("Total", total, Brushes.Black));
            return series;
        }

        // Single
        var g1 = opt.Gateway;
        if (g1 != null)
        {
            var s = g1.SpeedHistory.ToList();
            if (s.Count >= 2)
                series.Add(new Series(g1.Name, s, Brushes.Black));
        }

        return series;
    }

    private void PruneAllHistories()
    {
        if (Vm == null) return;

        var latest = DateTimeOffset.UtcNow;
        foreach (var g in Vm.CassiaGateways)
        {
            if (g.SpeedHistory.Count > 0)
            {
                var t = g.SpeedHistory[g.SpeedHistory.Count - 1].TimeUtc;
                if (t > latest) latest = t;
            }
        }

        var cutoff = latest - MaxHistory;
        foreach (var g in Vm.CassiaGateways)
            PruneHistory(g, cutoff);
    }

    private static void PruneHistory(CassiaGateway gw, DateTimeOffset cutoffUtc)
    {
        try
        {
            while (gw.SpeedHistory.Count > 0 && gw.SpeedHistory[0].TimeUtc < cutoffUtc)
                gw.SpeedHistory.RemoveAt(0);
        }
        catch
        {
            // Best-effort pruning only.
        }
    }

    private void UpdateAverages(IReadOnlyList<Series> series, DateTimeOffset maxT)
    {
        var agg = BuildAggregateSeries(series, maxT);

        var avg15 = AverageOver(agg, maxT - TimeSpan.FromMinutes(15), maxT);
        var avg30 = AverageOver(agg, maxT - TimeSpan.FromMinutes(30), maxT);

        Avg15Text.Text = double.IsNaN(avg15) ? "—" : $"{avg15:0.##} %/min";
        Avg30Text.Text = double.IsNaN(avg30) ? "—" : $"{avg30:0.##} %/min";
    }

    private static List<SpeedSample> BuildAggregateSeries(IReadOnlyList<Series> series, DateTimeOffset maxT)
    {
        if (series.Count == 1)
            return series[0].Samples.OrderBy(s => s.TimeUtc).ToList();

        var minT = maxT - MaxHistory;

        var histories = series
            .Select(s => s.Samples.OrderBy(x => x.TimeUtc).ToList())
            .ToList();

        var result = new List<SpeedSample>();

        for (var t = minT; t <= maxT; t += TimeSpan.FromMinutes(1))
        {
            double sum = 0;
            bool any = false;

            for (int i = 0; i < histories.Count; i++)
            {
                var h = histories[i];
                if (h.Count == 0) continue;
                var idx = h.FindLastIndex(s => s.TimeUtc <= t);
                if (idx >= 0)
                {
                    sum += h[idx].SpeedPctPerMin;
                    any = true;
                }
            }

            if (any)
                result.Add(new SpeedSample(t, sum));
        }

        return result;
    }

    private static double AverageOver(IReadOnlyList<SpeedSample> samples, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        if (samples.Count == 0) return double.NaN;
        var inRange = samples.Where(s => s.TimeUtc >= fromUtc && s.TimeUtc <= toUtc).Select(s => s.SpeedPctPerMin).ToList();
        if (inRange.Count == 0) return double.NaN;
        return inRange.Average();
    }

    private static List<SpeedSample> BuildTotalSeries(IEnumerable<CassiaGateway> gateways, DateTimeOffset nowUtc, TimeSpan history)
    {
        var list = gateways?.ToList() ?? new List<CassiaGateway>();
        if (list.Count == 0) return new();

        var minT = nowUtc - history;

        // Pre-sort histories
        var histories = list
            .Select(g => g.SpeedHistory.OrderBy(s => s.TimeUtc).ToList())
            .Where(h => h.Count > 0)
            .ToList();

        if (histories.Count == 0)
            return new();

        // Timeline = union of all real sample times in the window (+ ensure we include 'nowUtc')
        var timeline = histories
            .SelectMany(h => h)
            .Where(s => s.TimeUtc >= minT && s.TimeUtc <= nowUtc)
            .Select(s => s.TimeUtc)
            .Distinct()
            .OrderBy(t => t)
            .ToList();

        if (timeline.Count == 0)
            return new();

        if (timeline[^1] < nowUtc)
            timeline.Add(nowUtc);

        // For each gateway keep an index that advances with time
        var idx = new int[histories.Count];
        var lastVal = new double[histories.Count];
        var hasVal = new bool[histories.Count];

        var result = new List<SpeedSample>(timeline.Count);

        foreach (var t in timeline)
        {
            double sum = 0;
            bool any = false;

            for (int i = 0; i < histories.Count; i++)
            {
                var h = histories[i];

                // Advance pointer while next sample <= t
                while (idx[i] < h.Count && h[idx[i]].TimeUtc <= t)
                {
                    lastVal[i] = h[idx[i]].SpeedPctPerMin;
                    hasVal[i] = true;
                    idx[i]++;
                }

                if (hasVal[i])
                {
                    sum += lastVal[i];
                    any = true;
                }
            }

            if (any)
                result.Add(new SpeedSample(t, sum));
        }

        return result;
    }


    private void DrawPlotBackground(double left, double top, double w, double h)
    {
        ChartCanvas.Children.Add(new Rectangle
        {
            Width = w,
            Height = h,
            RadiusX = 8,
            RadiusY = 8,
            Fill = new SolidColorBrush(Color.FromRgb(250, 250, 252)),
            Stroke = new SolidColorBrush(Color.FromRgb(230, 230, 235)),
            StrokeThickness = 1
        });

        Canvas.SetLeft(ChartCanvas.Children[^1], left);
        Canvas.SetTop(ChartCanvas.Children[^1], top);
    }

    private void DrawSeriesPolyline(Series s, double left, double top, double w, double h, DateTimeOffset minT, double dtSeconds, double yMax)
    {
        var pl = new Polyline
        {
            Stroke = s.Stroke,
            StrokeThickness = 2.2,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            SnapsToDevicePixels = true
        };

        foreach (var smp in s.Samples)
        {
            var x = left + ((smp.TimeUtc - minT).TotalSeconds / dtSeconds) * w;
            var y = top + (h - (smp.SpeedPctPerMin / yMax) * h);
            pl.Points.Add(new Point(x, y));
        }

        ChartCanvas.Children.Add(pl);
    }

    private void DrawAxes(double left, double top, double w, double h, DateTimeOffset minT, DateTimeOffset maxT, double yMax)
    {
        var x0 = left;
        var y0 = top + h;

        var gridStroke = new SolidColorBrush(Color.FromRgb(225, 225, 232));
        var axisStroke = new SolidColorBrush(Color.FromRgb(40, 40, 45));

        int yTicks = 5;
        for (int i = 0; i <= yTicks; i++)
        {
            var v = (yMax / yTicks) * i;
            var y = top + (h - (v / yMax) * h);

            ChartCanvas.Children.Add(new Line { X1 = x0, Y1 = y, X2 = x0 + w, Y2 = y, Stroke = gridStroke, StrokeThickness = 1 });
            ChartCanvas.Children.Add(new Line { X1 = x0 - 4, Y1 = y, X2 = x0, Y2 = y, Stroke = axisStroke, StrokeThickness = 1 });

            var tb = new TextBlock
            {
                Text = v.ToString("0", CultureInfo.InvariantCulture),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(70, 70, 75))
            };
            Canvas.SetLeft(tb, 6);
            Canvas.SetTop(tb, y - 8);
            ChartCanvas.Children.Add(tb);
        }

        var span = maxT - minT;
        var xTickCount = GetReasonableXTicks(span);

        for (int i = 0; i <= xTickCount; i++)
        {
            var t = minT + TimeSpan.FromTicks(span.Ticks * i / xTickCount);
            var x = left + (w * i / xTickCount);

            ChartCanvas.Children.Add(new Line { X1 = x, Y1 = top, X2 = x, Y2 = y0, Stroke = gridStroke, StrokeThickness = 1 });
            ChartCanvas.Children.Add(new Line { X1 = x, Y1 = y0, X2 = x, Y2 = y0 + 4, Stroke = axisStroke, StrokeThickness = 1 });

            var tb = new TextBlock
            {
                Text = t.ToLocalTime().ToString("HH:mm"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(70, 70, 75))
            };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tb, x - tb.DesiredSize.Width / 2);
            Canvas.SetTop(tb, y0 + 6);
            ChartCanvas.Children.Add(tb);
        }

        ChartCanvas.Children.Add(new Line { X1 = x0, Y1 = top, X2 = x0, Y2 = y0, Stroke = axisStroke, StrokeThickness = 1.2 });
        ChartCanvas.Children.Add(new Line { X1 = x0, Y1 = y0, X2 = x0 + w, Y2 = y0, Stroke = axisStroke, StrokeThickness = 1.2 });

        var yLabel = new TextBlock
        {
            Text = "% / min",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(60, 60, 65))
        };
        Canvas.SetLeft(yLabel, 10);
        Canvas.SetTop(yLabel, 2);
        ChartCanvas.Children.Add(yLabel);
    }

    private static int GetReasonableXTicks(TimeSpan span)
    {
        if (span.TotalMinutes <= 20) return 4;
        if (span.TotalMinutes <= 60) return 6;
        if (span.TotalHours <= 2) return 8;
        if (span.TotalHours <= 6) return 8;
        return 10;
    }

    private void DrawLegend(IReadOnlyList<Series> series, double leftX, double topY, DateTimeOffset maxT)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(190, 190, 195)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 12,
                Opacity = 0.18
            }
        };

        var stack = new StackPanel { Orientation = Orientation.Vertical };

        foreach (var s in series.Take(12))
        {
            var last = s.Samples.LastOrDefault();
            var avg15 = AverageOver(s.Samples, maxT - TimeSpan.FromMinutes(15), maxT);
            var avg30 = AverageOver(s.Samples, maxT - TimeSpan.FromMinutes(30), maxT);

            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };

            var swatch = new Rectangle
            {
                Width = 18,
                Height = 4,
                RadiusX = 2,
                RadiusY = 2,
                Fill = s.Stroke,
                Margin = new Thickness(0, 6, 8, 0)
            };
            DockPanel.SetDock(swatch, Dock.Left);
            row.Children.Add(swatch);

            var name = new TextBlock
            {
                Text = s.Name,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(40, 40, 45))
            };
            DockPanel.SetDock(name, Dock.Left);
            row.Children.Add(name);

            var stats = new TextBlock
            {
                Text = last == null
                    ? ""
                    : $"   last {last.SpeedPctPerMin:0.##}  |  15m {(double.IsNaN(avg15) ? "—" : avg15.ToString("0.##"))}  |  30m {(double.IsNaN(avg30) ? "—" : avg30.ToString("0.##"))}",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(90, 90, 95)),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            row.Children.Add(stats);

            stack.Children.Add(row);
        }

        border.Child = stack;

        ChartCanvas.Children.Add(border);

        border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(border, Math.Max(0, leftX));
        Canvas.SetTop(border, topY);
    }

    private static double NiceCeiling(double v)
    {
        var pow = Math.Pow(10, Math.Floor(Math.Log10(v)));
        var n = v / pow;
        double step = n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10;
        return step * pow;
    }

    // --------------------------
    // Timeframe selection UI
    // --------------------------

    private void TimeframeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TimeframeCombo.SelectedItem is ComboBoxItem item)
        {
            if ((item.Tag as string)?.Equals("CUSTOM", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (_customRangeUtc == null)
                    _customRangeUtc = (DateTimeOffset.UtcNow - TimeSpan.FromMinutes(30), DateTimeOffset.UtcNow);
            }
            else if (item.Tag is string ts && TimeSpan.TryParse(ts, CultureInfo.InvariantCulture, out var span))
            {
                _visibleWindow = span;
                _customRangeUtc = null;
            }
        }

        Redraw();
    }

    private void ResetZoom_Click(object sender, RoutedEventArgs e) => ResetZoom();

    private void ResetZoom()
    {
        _customRangeUtc = null;

        // if currently on Custom, return to 1 hour
        if (TimeframeCombo.SelectedItem is ComboBoxItem item &&
            (item.Tag as string)?.Equals("CUSTOM", StringComparison.OrdinalIgnoreCase) == true)
        {
            foreach (var it in TimeframeCombo.Items.OfType<ComboBoxItem>())
            {
                if ((it.Tag as string)?.Equals("01:00:00", StringComparison.OrdinalIgnoreCase) == true)
                {
                    TimeframeCombo.SelectedItem = it;
                    break;
                }
            }
        }

        Redraw();
    }

    // --------------------------
    // Drag-to-select (Custom)
    // --------------------------

    private void OverlayCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            ResetZoom();
            return;
        }

        var p = e.GetPosition(OverlayCanvas);
        if (!IsInsidePlot(p))
            return;

        _isSelecting = true;
        _selectStart = p;

        _selectRect = new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(40, 30, 144, 255)),
            Stroke = new SolidColorBrush(Color.FromArgb(160, 30, 144, 255)),
            StrokeThickness = 1,
            RadiusX = 6,
            RadiusY = 6,
            IsHitTestVisible = false
        };

        OverlayCanvas.Children.Add(_selectRect);
        OverlayCanvas.CaptureMouse();

        UpdateSelectionRect(p);
    }

    private void OverlayCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isSelecting || _selectRect == null) return;

        var p = ClampToPlot(e.GetPosition(OverlayCanvas));
        UpdateSelectionRect(p);
    }

    private void OverlayCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSelecting) return;

        _isSelecting = false;
        OverlayCanvas.ReleaseMouseCapture();

        var end = ClampToPlot(e.GetPosition(OverlayCanvas));
        ApplySelectionRange(_selectStart, end);

        OverlayCanvas.Children.Clear();
        _selectRect = null;

        Redraw();
    }

    private bool IsInsidePlot(Point p) =>
        p.X >= _plotLeft && p.X <= _plotLeft + _plotW &&
        p.Y >= _plotTop && p.Y <= _plotTop + _plotH;

    private Point ClampToPlot(Point p)
    {
        var x = Math.Max(_plotLeft, Math.Min(_plotLeft + _plotW, p.X));
        var y = Math.Max(_plotTop, Math.Min(_plotTop + _plotH, p.Y));
        return new Point(x, y);
    }

    private void UpdateSelectionRect(Point current)
    {
        if (_selectRect == null) return;

        var x1 = Math.Min(_selectStart.X, current.X);
        var x2 = Math.Max(_selectStart.X, current.X);

        Canvas.SetLeft(_selectRect, x1);
        Canvas.SetTop(_selectRect, _plotTop);
        _selectRect.Width = Math.Max(1, x2 - x1);
        _selectRect.Height = Math.Max(1, _plotH);
    }

    private void ApplySelectionRange(Point a, Point b)
    {
        var x1 = Math.Min(a.X, b.X);
        var x2 = Math.Max(a.X, b.X);

        if (Math.Abs(x2 - x1) < 8)
            return;

        var t1 = XToTimeUtc(x1);
        var t2 = XToTimeUtc(x2);

        if (t2 > t1)
            _customRangeUtc = (t1, t2);

        foreach (var it in TimeframeCombo.Items.OfType<ComboBoxItem>())
        {
            if ((it.Tag as string)?.Equals("CUSTOM", StringComparison.OrdinalIgnoreCase) == true)
            {
                TimeframeCombo.SelectedItem = it;
                break;
            }
        }
    }

    private DateTimeOffset XToTimeUtc(double x)
    {
        var pct = (_plotW <= 1) ? 0 : (x - _plotLeft) / _plotW;
        pct = Math.Max(0, Math.Min(1, pct));
        var seconds = (_plotMaxT - _plotMinT).TotalSeconds * pct;
        return _plotMinT + TimeSpan.FromSeconds(seconds);
    }
}
