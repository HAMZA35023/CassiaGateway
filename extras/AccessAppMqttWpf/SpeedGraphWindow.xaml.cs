using AccessAppMqttWpf.Models;
using AccessAppMqttWpf.ViewModels;
using System;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AccessAppMqttWpf;

public partial class SpeedGraphWindow : Window
{
    private MainViewModel? Vm => DataContext as MainViewModel;


    public SpeedGraphWindow(MainViewModel vm) : this()
    {
        DataContext = vm;
    }

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
    }

    private void Hook()
    {
        if (Vm == null) return;
        Vm.PropertyChanged += Vm_PropertyChanged;
        HookHistory();
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
        if (e.PropertyName == nameof(MainViewModel.SelectedSpeedGateway))
        {
            UnhookHistory();
            HookHistory();
            Redraw();
        }
    }

    private void HookHistory()
    {
        if (Vm == null) return;
        var gw = Vm.SelectedSpeedGateway;
        if (gw == null) return;

        // redraw when the selected gateway's history changes (single gateway view)
        gw.SpeedHistory.CollectionChanged += SpeedHistory_CollectionChanged;

        // in All/Total mode we need redraw when any gateway updates
        foreach (var g in Vm.CassiaGateways)
            g.SpeedHistory.CollectionChanged += SpeedHistory_CollectionChanged;
    }

    private void UnhookHistory()
    {
        if (Vm == null) return;
        foreach (var g in Vm.CassiaGateways)
            g.SpeedHistory.CollectionChanged -= SpeedHistory_CollectionChanged;

        var gw = Vm.SelectedSpeedGateway;
        if (gw != null)
            gw.SpeedHistory.CollectionChanged -= SpeedHistory_CollectionChanged;
    }

    private void SpeedHistory_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Redraw();

    private static bool IsAllMode(CassiaGateway gw) =>
        (gw.Name ?? "").Equals("(All gateways)", StringComparison.OrdinalIgnoreCase);

    private static bool IsTotalMode(CassiaGateway gw) =>
        (gw.Name ?? "").Equals("(Total)", StringComparison.OrdinalIgnoreCase);

    private sealed record Series(string Name, IReadOnlyList<SpeedSample> Samples, Brush Stroke);

    private void Redraw()
    {
        if (Vm == null) return;
        if (ChartCanvas == null) return;

        ChartCanvas.Children.Clear();

        var selection = Vm.SelectedSpeedGateway;
        if (selection == null)
        {
            ChartCanvas.Tag = "";
            return;
        }

        // ---- Build series ----
        var series = new List<Series>();
        var brushes = new Brush[]
        {
            Brushes.Black, Brushes.Blue, Brushes.Red, Brushes.Green, Brushes.DarkOrange,
            Brushes.Purple, Brushes.Brown, Brushes.DarkCyan, Brushes.Gray
        };

        if (IsAllMode(selection))
        {
            int bi = 0;
            foreach (var gw in Vm.CassiaGateways.OrderBy(g => g.Name))
            {
                var s = gw.SpeedHistory.ToList();
                if (s.Count < 2) continue;
                series.Add(new Series(gw.Name, s, brushes[bi++ % brushes.Length]));
            }
        }
        else if (IsTotalMode(selection))
        {
            var total = BuildTotalSeries(Vm.CassiaGateways);
            if (total.Count >= 2)
                series.Add(new Series("Total", total, Brushes.Black));
        }
        else
        {
            var s = selection.SpeedHistory.ToList();
            if (s.Count >= 2)
                series.Add(new Series(selection.Name, s, Brushes.Black));
        }

        if (series.Count == 0)
        {
            ChartCanvas.Tag = $"{selection.Name}: no speed history yet (need at least 2 samples)";
            return;
        }

        // ---- Determine plot range (last 1h) ----
        var maxT = series.SelectMany(s => s.Samples).Max(s => s.TimeUtc);
        var minT = maxT - TimeSpan.FromHours(1);

        // Trim to last hour
        series = series
            .Select(s => s with { Samples = s.Samples.Where(x => x.TimeUtc >= minT && x.TimeUtc <= maxT).ToList() })
            .Where(s => s.Samples.Count >= 2)
            .ToList();

        if (series.Count == 0)
        {
            ChartCanvas.Tag = $"{selection.Name}: no speed history in the last hour";
            return;
        }

        var yMaxRaw = series.SelectMany(s => s.Samples).Max(s => s.SpeedPctPerMin);
        var yMax = NiceCeiling(Math.Max(1, yMaxRaw));

        // ---- Layout ----
        double left = 55, right = 15, top = 15, bottom = 35;
        var cw = Math.Max(0, ChartCanvas.ActualWidth);
        var ch = Math.Max(0, ChartCanvas.ActualHeight);

        // In case the window was opened before a layout pass
        if (cw <= 10 || ch <= 10)
        {
            ChartCanvas.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            cw = Math.Max(0, ChartCanvas.DesiredSize.Width);
            ch = Math.Max(0, ChartCanvas.DesiredSize.Height);
        }

        var w = Math.Max(1, cw - left - right);
        var h = Math.Max(1, ch - top - bottom);

        var dt = Math.Max(1, (maxT - minT).TotalSeconds);

        // ---- Axes ----
        DrawAxes(left, top, w, h, minT, maxT, yMax);

        // ---- Series ----
        foreach (var s in series)
        {
            var pl = new Polyline
            {
                Stroke = s.Stroke,
                StrokeThickness = 2,
                SnapsToDevicePixels = true
            };

            foreach (var smp in s.Samples)
            {
                var x = left + ((smp.TimeUtc - minT).TotalSeconds / dt) * w;
                var y = top + (h - (smp.SpeedPctPerMin / yMax) * h);
                pl.Points.Add(new Point(x, y));
            }

            ChartCanvas.Children.Add(pl);
        }

        // ---- Legend ----
        if (series.Count > 1 || IsTotalMode(selection))
            DrawLegend(series, left + w - 10, top + 5);

        // ---- Caption ----
        var last = series.SelectMany(s => s.Samples).OrderBy(s => s.TimeUtc).Last();
        ChartCanvas.Tag = $"{selection.Name}: last {last.SpeedPctPerMin:0.##} %/min • range 0..{yMax:0} • series {series.Count}";
    }

    private static List<SpeedSample> BuildTotalSeries(IEnumerable<CassiaGateway> gateways)
    {
        var list = gateways.ToList();
        if (list.Count == 0) return new();

        var now = DateTimeOffset.UtcNow;
        var minT = now - TimeSpan.FromHours(1);

        // Pre-sort histories
        var histories = list
            .Select(g => g.SpeedHistory.OrderBy(s => s.TimeUtc).ToList())
            .ToList();

        var result = new List<SpeedSample>();

        // 1-minute resample for last hour
        for (var t = minT; t <= now; t += TimeSpan.FromMinutes(1))
        {
            double sum = 0;
            bool any = false;

            for (int i = 0; i < histories.Count; i++)
            {
                var h = histories[i];
                if (h.Count == 0) continue;

                // latest sample at or before t
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

    private void DrawAxes(double left, double top, double w, double h, DateTimeOffset minT, DateTimeOffset maxT, double yMax)
    {
        // Axis lines
        var x0 = left;
        var y0 = top + h;

        ChartCanvas.Children.Add(new Line { X1 = x0, Y1 = top, X2 = x0, Y2 = y0, Stroke = Brushes.Black, StrokeThickness = 1 });
        ChartCanvas.Children.Add(new Line { X1 = x0, Y1 = y0, X2 = x0 + w, Y2 = y0, Stroke = Brushes.Black, StrokeThickness = 1 });

        // Y ticks
        int yTicks = 5;
        for (int i = 0; i <= yTicks; i++)
        {
            var v = (yMax / yTicks) * i;
            var y = top + (h - (v / yMax) * h);

            ChartCanvas.Children.Add(new Line { X1 = x0 - 4, Y1 = y, X2 = x0, Y2 = y, Stroke = Brushes.Black, StrokeThickness = 1 });

            var tb = new TextBlock
            {
                Text = v.ToString("0"),
                FontSize = 11,
                Foreground = Brushes.Black
            };
            Canvas.SetLeft(tb, 0);
            Canvas.SetTop(tb, y - 8);
            ChartCanvas.Children.Add(tb);
        }

        // X ticks (minutes)
        int xTicks = 4;
        var span = maxT - minT;
        for (int i = 0; i <= xTicks; i++)
        {
            var t = minT + TimeSpan.FromTicks(span.Ticks * i / xTicks);
            var x = left + (w * i / xTicks);

            ChartCanvas.Children.Add(new Line { X1 = x, Y1 = y0, X2 = x, Y2 = y0 + 4, Stroke = Brushes.Black, StrokeThickness = 1 });

            var label = t.ToLocalTime().ToString("HH:mm");
            var tb = new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = Brushes.Black
            };
            Canvas.SetLeft(tb, x - 16);
            Canvas.SetTop(tb, y0 + 6);
            ChartCanvas.Children.Add(tb);
        }

        // Y axis label
        var yLabel = new TextBlock
        {
            Text = "% / min",
            FontSize = 12,
            Foreground = Brushes.Black
        };
        Canvas.SetLeft(yLabel, 5);
        Canvas.SetTop(yLabel, 2);
        ChartCanvas.Children.Add(yLabel);
    }

    private void DrawLegend(IReadOnlyList<Series> series, double rightX, double topY)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8)
        };

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        foreach (var s in series.Take(10))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            row.Children.Add(new Rectangle { Width = 18, Height = 3, Fill = s.Stroke, Margin = new Thickness(0, 6, 6, 0) });
            row.Children.Add(new TextBlock { Text = s.Name, FontSize = 12, Foreground = Brushes.Black });
            stack.Children.Add(row);
        }

        border.Child = stack;

        ChartCanvas.Children.Add(border);

        // measure and place at top-right inside plot
        border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var sz = border.DesiredSize;
        Canvas.SetLeft(border, Math.Max(0, rightX - sz.Width));
        Canvas.SetTop(border, topY);
    }

    private static double NiceCeiling(double v)
    {
        // Round up to a "nice" axis max (1,2,5 * 10^n).
        var pow = Math.Pow(10, Math.Floor(Math.Log10(v)));
        var n = v / pow;
        double step = n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10;
        return step * pow;
    }
}
