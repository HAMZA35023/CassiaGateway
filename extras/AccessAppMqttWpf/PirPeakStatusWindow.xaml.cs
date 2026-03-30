using AccessAppMqttWpf.Models;
using AccessAppMqttWpf.ViewModels;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace AccessAppMqttWpf;

public partial class PirPeakStatusWindow : Window, IDisposable
{
    private readonly MainViewModel _vm;

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
    private string? _preSelectMac;
    private bool _deviceMode; // true when opened for a specific device — hides the dropdowns

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

    public void PreSelectDevice(string cassiaName, string mac)
    {
        _preSelectCassia = cassiaName;
        _preSelectMac    = mac;
        _deviceMode      = true;
    }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm.PirPeakSampleReceived  += OnPirPeakSampleReceived;
        _vm.WalktestResultReceived += OnWalktestResultReceived;

        if (_deviceMode)
        {
            GatewayLabel.Visibility = Visibility.Collapsed;
            CassiaCombo.Visibility  = Visibility.Collapsed;
            DeviceLabel.Visibility  = Visibility.Collapsed;
            DeviceCombo.Visibility  = Visibility.Collapsed;

            _selectedCassiaName = _preSelectCassia;
            _selectedDeviceKey  = string.IsNullOrWhiteSpace(_preSelectMac) ? null
                                  : $"{_preSelectCassia}|{_preSelectMac}";
            var mac = _preSelectMac;
            _preSelectCassia = null;
            _preSelectMac    = null;

            if (_selectedDeviceKey != null && WalktestCheck != null)
                WalktestCheck.IsEnabled = true;

            Title = $"PIR Peak Status — {mac}";

            StartSession(_selectedCassiaName, mac);
        }
        else
        {
            PopulateCassiaCombo();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _vm.PirPeakSampleReceived  -= OnPirPeakSampleReceived;
        _vm.WalktestResultReceived -= OnWalktestResultReceived;
        StopCurrentSession();
    }

    public void Dispose() { }

    private void StartSession(string? cassiaName, string? mac)
    {
        if (string.IsNullOrWhiteSpace(cassiaName) || string.IsNullOrWhiteSpace(mac)) return;
        _ = _vm.SendStartPirPeakCommandAsync(cassiaName, mac);
    }

    private void StopCurrentSession()
    {
        if (_selectedCassiaName == null || _selectedDeviceKey == null) return;
        var idx = _selectedDeviceKey.IndexOf('|');
        if (idx < 0) return;
        var mac = _selectedDeviceKey[(idx + 1)..];
        _ = _vm.SendStopPirPeakCommandAsync(_selectedCassiaName, mac);
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
            StopCurrentSession();

            _selectedDeviceKey = key;
            _customRangeUtc = null;
            if (WalktestCheck != null) WalktestCheck.IsEnabled = true;
            Redraw();

            var idx = key.IndexOf('|');
            if (idx >= 0)
                StartSession(_selectedCassiaName, key[(idx + 1)..]);
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
            StopCurrentSession();
        else
            StartSession(_selectedCassiaName, GetSelectedMac());
    }

    private string? GetSelectedMac()
    {
        if (_selectedDeviceKey == null) return null;
        var idx = _selectedDeviceKey.IndexOf('|');
        return idx >= 0 ? _selectedDeviceKey[(idx + 1)..] : null;
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
    // Incoming samples
    // -------------------------------------------------------------------------

    private void OnPirPeakSampleReceived(string key, PirPeakSample sample)
    {
        if (!string.Equals(key, _selectedDeviceKey, StringComparison.OrdinalIgnoreCase))
            return;

        // Initialise walktest checkbox from the device state read at session start
        if (sample.WalktestActive.HasValue && !_walktestBusy && WalktestCheck != null)
        {
            _suppressWalktestEvent = true;
            WalktestCheck.IsChecked = sample.WalktestActive.Value;
            _suppressWalktestEvent = false;
        }

        if (!_paused)
            Redraw();
    }

    // -------------------------------------------------------------------------
    // Export: copy to clipboard / PDF
    // -------------------------------------------------------------------------

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (ChartBorder.ActualWidth < 1) return;
        try
        {
            Clipboard.SetImage(RenderExportBitmap());
            StatusText.Text = "Chart copied to clipboard.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Copy failed: {ex.Message}";
        }
    }

    private void ExportSvgButton_Click(object sender, RoutedEventArgs e)
    {
        if (ChartBorder.ActualWidth < 1) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title    = "Export PIR Peak Chart",
            Filter   = "SVG files (*.svg)|*.svg",
            FileName = $"PIR_Peak_{DateTime.Now:yyyyMMdd_HHmmss}.svg"
        };
        if (dlg.ShowDialog(this) != true) return;

        ExportSvgButton.IsEnabled = false;
        try
        {
            File.WriteAllText(dlg.FileName, GenerateChartSvg(), Encoding.UTF8);
            StatusText.Text = $"Exported {System.IO.Path.GetFileName(dlg.FileName)}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"SVG export failed: {ex.Message}";
        }
        finally
        {
            ExportSvgButton.IsEnabled = true;
        }
    }

    /// <summary>Generates a vector SVG of the chart canvas with a raster legend strip.</summary>
    private string GenerateChartSvg()
    {
        double cw  = ChartBorder.ActualWidth;
        double ch  = ChartBorder.ActualHeight;
        double lh  = LegendBorder.ActualHeight;
        const double gap = 8.0;
        double totalH = ch + gap + lh;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\"");
        sb.AppendLine($"     width=\"{cw:F0}\" height=\"{totalH:F0}\" viewBox=\"0 0 {cw:F2} {totalH:F2}\">");

        // Overall background
        sb.AppendLine($"  <rect width=\"{cw:F2}\" height=\"{totalH:F2}\" fill=\"white\"/>");

        // Chart border background
        sb.AppendLine($"  <rect x=\"0\" y=\"0\" width=\"{cw:F2}\" height=\"{ch:F2}\" rx=\"10\" ry=\"10\" fill=\"#FAFAFA\" stroke=\"#E6E6EB\" stroke-width=\"1\"/>");

        // All chart canvas children as vector elements
        foreach (UIElement el in ChartCanvas.Children)
            AppendSvgElement(sb, el, "  ");

        // Legend rendered as embedded PNG (StackPanel layout — too complex to vectorize)
        sb.AppendLine($"  <image x=\"0\" y=\"{ch + gap:F2}\" width=\"{cw:F2}\" height=\"{lh:F2}\"");
        sb.AppendLine($"         xlink:href=\"data:image/png;base64,{RenderLegendPng(cw, lh)}\"/>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private string RenderLegendPng(double w, double h)
    {
        var rtb = new RenderTargetBitmap((int)(w * 2), (int)(h * 2), 192, 192, PixelFormats.Pbgra32);
        var dv  = new DrawingVisual();
        using (var ctx = dv.RenderOpen())
            ctx.DrawRectangle(new VisualBrush(LegendBorder), null, new Rect(0, 0, w, h));
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>Renders chart + legend to a high-DPI bitmap (used for clipboard).</summary>
    private BitmapSource RenderExportBitmap(double scale = 2.0)
    {
        double totalW = ChartBorder.ActualWidth;
        double totalH = ChartBorder.ActualHeight + 8 + LegendBorder.ActualHeight;
        var rtb = new RenderTargetBitmap(
            (int)(totalW * scale), (int)(totalH * scale),
            96.0 * scale, 96.0 * scale, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var ctx = dv.RenderOpen())
        {
            ctx.DrawRectangle(Brushes.White, null, new Rect(0, 0, totalW, totalH));
            ctx.DrawRectangle(new VisualBrush(ChartBorder),  null,
                new Rect(0, 0, ChartBorder.ActualWidth, ChartBorder.ActualHeight));
            ctx.DrawRectangle(new VisualBrush(LegendBorder), null,
                new Rect(0, ChartBorder.ActualHeight + 8, LegendBorder.ActualWidth, LegendBorder.ActualHeight));
        }
        rtb.Render(dv);
        return rtb;
    }

    // -------------------------------------------------------------------------
    // SVG element writers
    // -------------------------------------------------------------------------

    private static void AppendSvgElement(StringBuilder sb, UIElement el, string indent)
    {
        switch (el)
        {
            case Line l:
            {
                sb.Append($"{indent}<line x1=\"{l.X1:F2}\" y1=\"{l.Y1:F2}\" x2=\"{l.X2:F2}\" y2=\"{l.Y2:F2}\"");
                sb.Append($" {SvgStroke(l.Stroke, l.StrokeThickness)}");
                if (l.StrokeDashArray?.Count > 0)
                    sb.Append($" stroke-dasharray=\"{SvgDashArray(l.StrokeDashArray, l.StrokeThickness)}\"");
                sb.AppendLine("/>");
                break;
            }
            case Polyline pl when pl.Points.Count >= 2:
                sb.AppendLine($"{indent}<polyline points=\"{SvgPoints(pl.Points)}\"" +
                              $" {SvgStroke(pl.Stroke, pl.StrokeThickness)} fill=\"none\"" +
                              " stroke-linejoin=\"round\" stroke-linecap=\"round\"/>");
                break;

            case Polygon pg when pg.Points.Count >= 3:
                sb.AppendLine($"{indent}<polygon points=\"{SvgPoints(pg.Points)}\"" +
                              $" {SvgFill(pg.Fill)} {SvgStroke(pg.Stroke, pg.StrokeThickness)}/>");
                break;

            case Rectangle r:
            {
                var x = Canvas.GetLeft(r); if (double.IsNaN(x)) x = 0;
                var y = Canvas.GetTop(r);  if (double.IsNaN(y)) y = 0;
                sb.AppendLine($"{indent}<rect x=\"{x:F2}\" y=\"{y:F2}\" width=\"{r.Width:F2}\" height=\"{r.Height:F2}\"" +
                              $" rx=\"{r.RadiusX:F1}\" ry=\"{r.RadiusY:F1}\"" +
                              $" {SvgFill(r.Fill)} {SvgStroke(r.Stroke, r.StrokeThickness)}/>");
                break;
            }
            case TextBlock tb:
            {
                var x = Canvas.GetLeft(tb); if (double.IsNaN(x)) x = 0;
                var y = Canvas.GetTop(tb);  if (double.IsNaN(y)) y = 0;
                // SVG text y = baseline; WPF Canvas.Top = top of element; approximate ascender ≈ 0.82 × fontSize
                var baseline = y + tb.FontSize * 0.82;
                sb.AppendLine($"{indent}<text x=\"{x:F2}\" y=\"{baseline:F2}\"" +
                              $" font-family=\"Segoe UI,sans-serif\" font-size=\"{tb.FontSize:F1}\"" +
                              $" {SvgFill(tb.Foreground)}>{SvgEscape(tb.Text)}</text>");
                break;
            }
        }
    }

    private static string SvgStroke(Brush? brush, double width)
    {
        if (brush == null) return "stroke=\"none\"";
        return $"stroke=\"{SvgRgb(brush)}\" stroke-opacity=\"{SvgAlpha(brush):F3}\" stroke-width=\"{width:F2}\"";
    }

    private static string SvgFill(Brush? brush)
    {
        if (brush == null || SvgAlpha(brush) < 0.004) return "fill=\"none\"";
        return $"fill=\"{SvgRgb(brush)}\" fill-opacity=\"{SvgAlpha(brush):F3}\"";
    }

    private static string SvgRgb(Brush brush) =>
        brush is SolidColorBrush scb
            ? $"#{scb.Color.R:X2}{scb.Color.G:X2}{scb.Color.B:X2}"
            : "none";

    private static double SvgAlpha(Brush brush) =>
        brush is SolidColorBrush scb ? scb.Color.A / 255.0 : 1.0;

    private static string SvgPoints(PointCollection pts) =>
        string.Join(" ", pts.Select(static p => $"{p.X:F2},{p.Y:F2}"));

    private static string SvgDashArray(DoubleCollection arr, double width) =>
        string.Join(",", arr.Select(d => (d * width).ToString("F2", CultureInfo.InvariantCulture)));

    private static string SvgEscape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

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
            // Extend Y axis to always show trigger levels, even when signal is below them
            // 0xFFFF (65535) means "disabled" — skip it
            var ls = samples[^1];
            if (ls.TrigA.HasValue && ls.TrigA.Value != ushort.MaxValue) yMax = Math.Max(yMax, ls.TrigA.Value);
            if (ls.TrigB.HasValue && ls.TrigB.Value != ushort.MaxValue) yMax = Math.Max(yMax, ls.TrigB.Value);
            if (ls.TrigC.HasValue && ls.TrigC.Value != ushort.MaxValue) yMax = Math.Max(yMax, ls.TrigC.Value);
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

        // Trigger level lines (horizontal, from opcode 0x0240 — latest sample's TrigA/B/C)
        // 0xFFFF (65535) means "disabled" — skip it
        var last = samples[^1];
        if (last.TrigA.HasValue && last.TrigA.Value != ushort.MaxValue) DrawTriggerLine(last.TrigA.Value, left, top, w, h, yMin, yRange, BrushA);
        if (last.TrigB.HasValue && last.TrigB.Value != ushort.MaxValue) DrawTriggerLine(last.TrigB.Value, left, top, w, h, yMin, yRange, BrushB);
        if (last.TrigC.HasValue && last.TrigC.Value != ushort.MaxValue) DrawTriggerLine(last.TrigC.Value, left, top, w, h, yMin, yRange, BrushC);
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

    private void DrawTriggerLine(uint trigValue, double left, double top, double w, double h,
                                 double yMin, double yRange, Brush brush)
    {
        var v    = (double)trigValue;
        var norm = Math.Max(0, Math.Min(1, (v - yMin) / yRange));
        var y    = top + (h - norm * h);

        ChartCanvas.Children.Add(new Line
        {
            X1 = left, Y1 = y, X2 = left + w, Y2 = y,
            Stroke = ThemeAlphaBrush(brush, 180),
            StrokeThickness = 1.2,
            StrokeDashArray = new System.Windows.Media.DoubleCollection { 6, 3 },
            SnapsToDevicePixels = true
        });
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
