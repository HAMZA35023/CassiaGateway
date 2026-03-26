using AccessAppMqttWpf.Models;
using AccessAppMqttWpf.ViewModels;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AccessAppMqttWpf;

public partial class PirPeakStatusWindow : Window, IDisposable
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _pollTimer;

    // Visible time window and custom drag-zoom range
    private TimeSpan _visibleWindow = TimeSpan.FromMinutes(10);
    private (DateTimeOffset Min, DateTimeOffset Max)? _customRangeUtc;

    // Drag-to-zoom overlay state
    private bool _isSelecting;
    private Point _selectStart;
    private Rectangle? _selectRect;

    // Last plot geometry for coordinate mapping
    private double _plotLeft, _plotTop, _plotW, _plotH;
    private DateTimeOffset _plotMinT, _plotMaxT;

    // Pause state
    private bool _paused;

    // Walktest
    private bool _walktestBusy;
    private bool _suppressWalktestEvent;

    // Pre-select request (set before window loads)
    private string? _preSelectCassia;

    // Current selection keys
    private string? _selectedCassiaName;
    private string? _selectedDeviceKey; // "cassiaName|MAC"

    private static readonly Brush BrushA = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly Brush BrushB = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
    private static readonly Brush BrushC = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));

    public PirPeakStatusWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _pollTimer.Tick += PollTimer_Tick;

        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => Redraw();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { ResetZoom(); e.Handled = true; }
            else if (e.Key == Key.F5) { Redraw(); e.Handled = true; }
        };
    }

    // -------------------------------------------------------------------------
    // Pre-selection API (called before Show())
    // -------------------------------------------------------------------------

    public void PreSelectCassia(string cassiaName)
    {
        _preSelectCassia = cassiaName;
    }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm.PirPeakSampleReceived += OnPirPeakSampleReceived;
        _vm.WalktestResultReceived += OnWalktestResultReceived;

        PopulateCassiaCombo();

        _pollTimer.Start();
        SendPoll();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _pollTimer.Stop();
        _vm.PirPeakSampleReceived -= OnPirPeakSampleReceived;
        _vm.WalktestResultReceived -= OnWalktestResultReceived;
    }

    public void Dispose()
    {
        _pollTimer.Stop();
    }

    // -------------------------------------------------------------------------
    // Combo population
    // -------------------------------------------------------------------------

    private void PopulateCassiaCombo()
    {
        // Gather distinct cassia names from known keys + live gateways
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var gw in _vm.CassiaGateways)
            names.Add(gw.Name);

        foreach (var key in _vm.GetPirPeakKeys())
        {
            var parts = key.Split('|');
            if (parts.Length >= 1) names.Add(parts[0]);
        }

        var sorted = names.OrderBy(n => n).ToList();

        CassiaCombo.ItemsSource = sorted;

        // Honour pre-selection or keep current
        if (_preSelectCassia != null && sorted.Contains(_preSelectCassia, StringComparer.OrdinalIgnoreCase))
        {
            CassiaCombo.SelectedItem = sorted.First(n => string.Equals(n, _preSelectCassia, StringComparison.OrdinalIgnoreCase));
            _preSelectCassia = null;
        }
        else if (_selectedCassiaName != null && sorted.Contains(_selectedCassiaName, StringComparer.OrdinalIgnoreCase))
        {
            CassiaCombo.SelectedItem = sorted.First(n => string.Equals(n, _selectedCassiaName, StringComparison.OrdinalIgnoreCase));
        }
        else if (sorted.Count > 0)
        {
            CassiaCombo.SelectedIndex = 0;
        }
    }

    private void PopulateDeviceCombo(string cassiaName)
    {
        // Keys matching "cassiaName|*"
        var prefix = cassiaName + "|";
        var keys = _vm.GetPirPeakKeys()
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k)
            .ToList();

        DeviceCombo.ItemsSource = keys;

        if (_selectedDeviceKey != null && keys.Contains(_selectedDeviceKey, StringComparer.OrdinalIgnoreCase))
            DeviceCombo.SelectedItem = keys.First(k => string.Equals(k, _selectedDeviceKey, StringComparison.OrdinalIgnoreCase));
        else if (keys.Count > 0)
            DeviceCombo.SelectedIndex = 0;
        else
            DeviceCombo.SelectedIndex = -1;
    }

    // -------------------------------------------------------------------------
    // Event handlers – combo selectors
    // -------------------------------------------------------------------------

    private void CassiaCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CassiaCombo.SelectedItem is string name)
        {
            _selectedCassiaName = name;
            PopulateDeviceCombo(name);
        }
    }

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceCombo.SelectedItem is string key)
        {
            _selectedDeviceKey = key;
            _customRangeUtc = null;
            // Enable walktest checkbox once a device is selected
            if (WalktestCheck != null) WalktestCheck.IsEnabled = true;
            Redraw();
        }
    }

    private void TimeframeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TimeframeCombo.SelectedItem is ComboBoxItem item &&
            item.Tag is string ts &&
            TimeSpan.TryParse(ts, CultureInfo.InvariantCulture, out var span))
        {
            _visibleWindow = span;
            _customRangeUtc = null;
        }
        Redraw();
    }

    // -------------------------------------------------------------------------
    // Button handlers
    // -------------------------------------------------------------------------

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        _paused = !_paused;
        PauseButton.Content = _paused ? "Resume" : "Pause";
        if (_paused)
            _pollTimer.Stop();
        else
        {
            _pollTimer.Start();
            SendPoll();
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _vm.ClearPirPeakHistory(_selectedDeviceKey);
        _customRangeUtc = null;
        Redraw();
    }

    private void ShowMinMaxCheck_Changed(object sender, RoutedEventArgs e) => Redraw();

    private async void WalktestCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressWalktestEvent) return;
        if (_walktestBusy) return;
        if (_selectedCassiaName == null || _selectedDeviceKey == null) return;

        var idx = _selectedDeviceKey.IndexOf('|');
        if (idx < 0) return;
        var mac = _selectedDeviceKey[(idx + 1)..];

        bool desired = WalktestCheck.IsChecked == true;
        _walktestBusy = true;
        WalktestCheck.IsEnabled = false;
        StatusText.Text = $"Setting walktest {(desired ? "on" : "off")} for {mac}…";

        await _vm.SendSetWalktestCommandAsync(_selectedCassiaName, mac, desired);
        // Result handled in OnWalktestResultReceived — re-enable after timeout in case result never arrives
        _ = System.Threading.Tasks.Task.Delay(15_000).ContinueWith(_ => Dispatcher.Invoke(() =>
        {
            if (_walktestBusy) { _walktestBusy = false; WalktestCheck.IsEnabled = true; }
        }));
    }

    private void OnWalktestResultReceived(string key, bool enabled, bool success, string? error)
    {
        if (!string.Equals(key, _selectedDeviceKey, StringComparison.OrdinalIgnoreCase)) return;

        _walktestBusy = false;
        WalktestCheck.IsEnabled = true;

        if (success)
        {
            _suppressWalktestEvent = true;
            WalktestCheck.IsChecked = enabled;
            _suppressWalktestEvent = false;
            StatusText.Text = $"Walktest {(enabled ? "enabled" : "disabled")}";
        }
        else
        {
            // Revert checkbox
            _suppressWalktestEvent = true;
            WalktestCheck.IsChecked = !enabled;
            _suppressWalktestEvent = false;
            StatusText.Text = $"Walktest {(enabled ? "enable" : "disable")} failed: {error}";
        }
    }

    // -------------------------------------------------------------------------
    // Polling
    // -------------------------------------------------------------------------

    private async void PollTimer_Tick(object? sender, EventArgs e) => await SendPollAsync();

    private void SendPoll() => _ = SendPollAsync();

    private async System.Threading.Tasks.Task SendPollAsync()
    {
        if (_selectedCassiaName == null) return;
        if (_selectedDeviceKey == null) return;

        // Extract MAC from key "cassiaName|MAC"
        var idx = _selectedDeviceKey.IndexOf('|');
        if (idx < 0) return;
        var mac = _selectedDeviceKey[(idx + 1)..];

        await _vm.SendGetPirPeakCommandAsync(_selectedCassiaName, new[] { mac });
    }

    // -------------------------------------------------------------------------
    // Incoming samples
    // -------------------------------------------------------------------------

    private void OnPirPeakSampleReceived(string key, PirPeakSample sample)
    {
        // Refresh device combo in case a new device appeared
        if (_selectedCassiaName != null)
            PopulateDeviceCombo(_selectedCassiaName);

        if (!string.Equals(key, _selectedDeviceKey, StringComparison.OrdinalIgnoreCase))
            return;

        if (!_paused)
            Redraw();
    }

    // -------------------------------------------------------------------------
    // Chart drawing
    // -------------------------------------------------------------------------

    private void Redraw()
    {
        if (ChartCanvas == null) return;

        ChartCanvas.Children.Clear();
        OverlayCanvas.Children.Clear();

        if (_selectedDeviceKey == null)
        {
            StatusText.Text = "Select a gateway and device.";
            return;
        }

        var allSamples = _vm.GetPirPeakHistory(_selectedDeviceKey);
        if (allSamples.Count == 0)
        {
            StatusText.Text = "Waiting for data…";
            return;
        }

        var maxT = allSamples.Max(s => s.TimeUtc);
        var minT = maxT - _visibleWindow;

        if (_customRangeUtc is { } cr)
        {
            var cMin = cr.Min < maxT - TimeSpan.FromHours(1) ? maxT - TimeSpan.FromHours(1) : cr.Min;
            var cMax = cr.Max > maxT ? maxT : cr.Max;
            if (cMax > cMin) { minT = cMin; maxT = cMax; }
        }

        var samples = allSamples.Where(s => s.TimeUtc >= minT && s.TimeUtc <= maxT).ToList();

        if (samples.Count == 0)
        {
            StatusText.Text = "No samples in selected range.";
            return;
        }

        // Layout
        const double left = 56, right = 18, top = 18, bottom = 38;
        var cw = Math.Max(0, ChartCanvas.ActualWidth);
        var ch = Math.Max(0, ChartCanvas.ActualHeight);
        var w  = Math.Max(1, cw - left - right);
        var h  = Math.Max(1, ch - top - bottom);
        var dt = Math.Max(1.0, (maxT - minT).TotalSeconds);

        _plotLeft = left; _plotTop = top; _plotW = w; _plotH = h;
        _plotMinT = minT; _plotMaxT = maxT;

        bool showMinMax = ShowMinMaxCheck?.IsChecked == true;

        // Y-axis scale: span across all three deltas + optional min/max raw values
        double yMin, yMax;
        if (showMinMax)
        {
            var allVals = samples.SelectMany(s => new[] { (double)s.AMin, s.AMax, s.BMin, s.BMax, s.CMin, s.CMax });
            yMin = allVals.Min();
            yMax = allVals.Max();
        }
        else
        {
            yMin = 0;
            yMax = samples.Max(s => Math.Max(s.ADelta, Math.Max(s.BDelta, s.CDelta)));
        }

        if (yMax <= yMin) yMax = yMin + 1;
        var yRange = yMax - yMin;

        DrawPlotBackground(left, top, w, h);
        DrawAxes(left, top, w, h, minT, maxT, yMin, yMax);

        if (showMinMax)
        {
            DrawBandPolyline(samples, s => s.AMin, s => s.AMax, left, top, w, h, minT, dt, yMin, yRange, BrushA);
            DrawBandPolyline(samples, s => s.BMin, s => s.BMax, left, top, w, h, minT, dt, yMin, yRange, BrushB);
            DrawBandPolyline(samples, s => s.CMin, s => s.CMax, left, top, w, h, minT, dt, yMin, yRange, BrushC);
        }

        DrawPolyline(samples, s => s.ADelta, left, top, w, h, minT, dt, yMin, yRange, BrushA, 2.2);
        DrawPolyline(samples, s => s.BDelta, left, top, w, h, minT, dt, yMin, yRange, BrushB, 2.2);
        DrawPolyline(samples, s => s.CDelta, left, top, w, h, minT, dt, yMin, yRange, BrushC, 2.2);

        // Update header labels from latest sample
        var last = samples[^1];
        TickText.Text   = last.TickCount.ToString();
        DeltaAText.Text = last.ADelta.ToString("0.##");
        DeltaBText.Text = last.BDelta.ToString("0.##");
        DeltaCText.Text = last.CDelta.ToString("0.##");
        LastUpdateText.Text = $"@ {last.TimeUtc.ToLocalTime():HH:mm:ss}";

        StatusText.Text = $"{samples.Count} samples • {minT.ToLocalTime():HH:mm} – {maxT.ToLocalTime():HH:mm} • y {yMin:0.#}…{yMax:0.#}";
    }

    private void DrawPlotBackground(double left, double top, double w, double h)
    {
        var rect = new Rectangle
        {
            Width  = w,
            Height = h,
            RadiusX = 8, RadiusY = 8,
            Fill   = ThemeBrush("Card2Brush", Color.FromRgb(250, 250, 252)),
            Stroke = ThemeBrush("BorderBrush", Color.FromRgb(230, 230, 235)),
            StrokeThickness = 1
        };
        Canvas.SetLeft(rect, left);
        Canvas.SetTop(rect, top);
        ChartCanvas.Children.Add(rect);
    }

    private void DrawAxes(double left, double top, double w, double h,
                          DateTimeOffset minT, DateTimeOffset maxT, double yMin, double yMax)
    {
        var gridStroke = ThemeBrush("BorderBrush", Color.FromRgb(225, 225, 232));
        var axisStroke = ThemeBrush("MutedBrush",  Color.FromRgb(40, 40, 45));
        var labelFore  = ThemeBrush("MutedBrush",  Color.FromRgb(70, 70, 75));

        double x0 = left, y0 = top + h;
        var yRange = yMax - yMin;

        // Y grid + labels (5 ticks)
        for (int i = 0; i <= 5; i++)
        {
            var v = yMin + yRange * i / 5.0;
            var y = top + (h - (v - yMin) / yRange * h);

            ChartCanvas.Children.Add(new Line { X1 = x0, Y1 = y, X2 = x0 + w, Y2 = y, Stroke = gridStroke, StrokeThickness = 1 });
            ChartCanvas.Children.Add(new Line { X1 = x0 - 4, Y1 = y, X2 = x0, Y2 = y, Stroke = axisStroke, StrokeThickness = 1 });

            var tb = new TextBlock { Text = v.ToString("0.#", CultureInfo.InvariantCulture), FontSize = 11, Foreground = labelFore };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tb, left - tb.DesiredSize.Width - 6);
            Canvas.SetTop(tb, y - 8);
            ChartCanvas.Children.Add(tb);
        }

        // X grid + labels
        var span = maxT - minT;
        int xTicks = span.TotalMinutes <= 5 ? 5 : span.TotalMinutes <= 15 ? 5 : span.TotalMinutes <= 60 ? 6 : 8;

        for (int i = 0; i <= xTicks; i++)
        {
            var t = minT + TimeSpan.FromTicks(span.Ticks * i / xTicks);
            var x = left + w * i / xTicks;

            ChartCanvas.Children.Add(new Line { X1 = x, Y1 = top, X2 = x, Y2 = y0, Stroke = gridStroke, StrokeThickness = 1 });
            ChartCanvas.Children.Add(new Line { X1 = x, Y1 = y0, X2 = x, Y2 = y0 + 4, Stroke = axisStroke, StrokeThickness = 1 });

            var tb = new TextBlock { Text = t.ToLocalTime().ToString("HH:mm:ss"), FontSize = 11, Foreground = labelFore };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tb, x - tb.DesiredSize.Width / 2);
            Canvas.SetTop(tb, y0 + 6);
            ChartCanvas.Children.Add(tb);
        }

        // Axes
        ChartCanvas.Children.Add(new Line { X1 = x0, Y1 = top, X2 = x0, Y2 = y0, Stroke = axisStroke, StrokeThickness = 1.2 });
        ChartCanvas.Children.Add(new Line { X1 = x0, Y1 = y0, X2 = x0 + w, Y2 = y0, Stroke = axisStroke, StrokeThickness = 1.2 });
    }

    private void DrawPolyline(IReadOnlyList<PirPeakSample> samples,
                              Func<PirPeakSample, float> valueSelector,
                              double left, double top, double w, double h,
                              DateTimeOffset minT, double dtSec,
                              double yMin, double yRange,
                              Brush stroke, double thickness)
    {
        var pl = new Polyline
        {
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            SnapsToDevicePixels = true
        };

        foreach (var s in samples)
        {
            var x = left + (s.TimeUtc - minT).TotalSeconds / dtSec * w;
            var y = top + (h - (valueSelector(s) - yMin) / yRange * h);
            pl.Points.Add(new Point(x, y));
        }

        ChartCanvas.Children.Add(pl);
    }

    private void DrawBandPolyline(IReadOnlyList<PirPeakSample> samples,
                                  Func<PirPeakSample, float> minSel,
                                  Func<PirPeakSample, float> maxSel,
                                  double left, double top, double w, double h,
                                  DateTimeOffset minT, double dtSec,
                                  double yMin, double yRange,
                                  Brush stroke)
    {
        // Draw as a filled polygon: forward pass (max), then reverse pass (min)
        var pts = new PointCollection();

        foreach (var s in samples)
        {
            var x = left + (s.TimeUtc - minT).TotalSeconds / dtSec * w;
            var y = top + (h - (maxSel(s) - yMin) / yRange * h);
            pts.Add(new Point(x, y));
        }

        for (int i = samples.Count - 1; i >= 0; i--)
        {
            var s = samples[i];
            var x = left + (s.TimeUtc - minT).TotalSeconds / dtSec * w;
            var y = top + (h - (minSel(s) - yMin) / yRange * h);
            pts.Add(new Point(x, y));
        }

        var poly = new Polygon
        {
            Points = pts,
            Fill = ThemeAlphaBrush(stroke, 35),
            Stroke = ThemeAlphaBrush(stroke, 100),
            StrokeThickness = 1,
            SnapsToDevicePixels = true
        };

        ChartCanvas.Children.Add(poly);
    }

    // -------------------------------------------------------------------------
    // Drag-to-zoom on overlay canvas
    // -------------------------------------------------------------------------

    private void OverlayCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2) { ResetZoom(); return; }

        var p = e.GetPosition(OverlayCanvas);
        if (!IsInsidePlot(p)) return;

        _isSelecting = true;
        _selectStart = p;

        _selectRect = new Rectangle
        {
            Fill = ThemeAlphaBrush("AccentBrush", 40, Color.FromRgb(30, 144, 255)),
            Stroke = ThemeAlphaBrush("AccentBrush", 160, Color.FromRgb(30, 144, 255)),
            StrokeThickness = 1,
            RadiusX = 4, RadiusY = 4,
            IsHitTestVisible = false
        };
        OverlayCanvas.Children.Add(_selectRect);
        OverlayCanvas.CaptureMouse();
        UpdateSelectionRect(p);
    }

    private void OverlayCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isSelecting || _selectRect == null) return;
        UpdateSelectionRect(ClampToPlot(e.GetPosition(OverlayCanvas)));
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
        p.Y >= _plotTop  && p.Y <= _plotTop  + _plotH;

    private Point ClampToPlot(Point p) =>
        new(Math.Max(_plotLeft, Math.Min(_plotLeft + _plotW, p.X)),
            Math.Max(_plotTop,  Math.Min(_plotTop  + _plotH, p.Y)));

    private void UpdateSelectionRect(Point current)
    {
        if (_selectRect == null) return;
        var x1 = Math.Min(_selectStart.X, current.X);
        var x2 = Math.Max(_selectStart.X, current.X);
        Canvas.SetLeft(_selectRect, x1);
        Canvas.SetTop(_selectRect, _plotTop);
        _selectRect.Width  = Math.Max(1, x2 - x1);
        _selectRect.Height = Math.Max(1, _plotH);
    }

    private void ApplySelectionRange(Point a, Point b)
    {
        var x1 = Math.Min(a.X, b.X);
        var x2 = Math.Max(a.X, b.X);
        if (Math.Abs(x2 - x1) < 8) return;

        var t1 = XToTimeUtc(x1);
        var t2 = XToTimeUtc(x2);
        if (t2 > t1) _customRangeUtc = (t1, t2);
    }

    private void ResetZoom()
    {
        _customRangeUtc = null;
        Redraw();
    }

    private DateTimeOffset XToTimeUtc(double x)
    {
        var pct = _plotW <= 1 ? 0 : (x - _plotLeft) / _plotW;
        pct = Math.Max(0, Math.Min(1, pct));
        return _plotMinT + TimeSpan.FromSeconds((_plotMaxT - _plotMinT).TotalSeconds * pct);
    }

    // -------------------------------------------------------------------------
    // Theme helpers
    // -------------------------------------------------------------------------

    private static Brush ThemeBrush(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is Brush b) return b;
        return new SolidColorBrush(fallback);
    }

    private static SolidColorBrush ThemeAlphaBrush(string key, byte alpha, Color fallback)
    {
        var color = fallback;
        if (Application.Current?.TryFindResource(key) is SolidColorBrush b) color = b.Color;
        return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
    }

    private static SolidColorBrush ThemeAlphaBrush(Brush sourceBrush, byte alpha)
    {
        var color = sourceBrush is SolidColorBrush scb ? scb.Color : Colors.Gray;
        return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
    }
}
