using AccessAppMqttWpf.Models;
using AccessAppMqttWpf.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AccessAppMqttWpf;

public partial class DetectorSettingsWindow : Window
{
    private const int MinKelvin = 1800;
    private const int MaxKelvin = 6500;
    private const int MinLux = 20;
    private const int MaxLux = 2000;
    private const int KelvinStep = 100;
    private const int LuxStep = 10;

    private const double PlotLeft = 28d;
    private const double PlotRight = 12d;
    private const double PlotTop = 12d;
    private const double PlotBottom = 24d;

    private readonly DetectorSettingsViewModel _vm;
    private bool _isKelvinDragging;
    private bool _isLuxDragging;
    private int _dragKelvinHour = -1;
    private int _dragLuxHour = -1;

    public DetectorSettingsWindow(MainViewModel main, DiscoveredDevice? device)
    {
        InitializeComponent();
        _vm = new DetectorSettingsViewModel(main, device);
        DataContext = _vm;
        Title = _vm.WindowTitle;

        _vm.TunableWhiteHourPoints.CollectionChanged += OnTunableWhiteHourPointsCollectionChanged;
        AttachPointHandlers(_vm.TunableWhiteHourPoints);
        _vm.RequestClose += OnRequestClose;

        Loaded += OnLoaded;
        Closed += (_, __) =>
        {
            DetachPointHandlers(_vm.TunableWhiteHourPoints);
            _vm.TunableWhiteHourPoints.CollectionChanged -= OnTunableWhiteHourPointsCollectionChanged;
            _vm.RequestClose -= OnRequestClose;
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RedrawScheduleGraphs();
    }

    private void OnRequestClose()
    {
        try { Close(); } catch { }
    }

    private void OnTunableWhiteHourPointsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            DetachPointHandlers(e.OldItems.OfType<TunableWhiteHourPointViewModel>());
        if (e.NewItems != null)
            AttachPointHandlers(e.NewItems.OfType<TunableWhiteHourPointViewModel>());
        RedrawScheduleGraphs();
    }

    private void AttachPointHandlers(IEnumerable<TunableWhiteHourPointViewModel> points)
    {
        foreach (var point in points)
            point.PropertyChanged += OnPointPropertyChanged;
    }

    private void DetachPointHandlers(IEnumerable<TunableWhiteHourPointViewModel> points)
    {
        foreach (var point in points)
            point.PropertyChanged -= OnPointPropertyChanged;
    }

    private void OnPointPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(TunableWhiteHourPointViewModel.Kelvin), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(TunableWhiteHourPointViewModel.Lux), StringComparison.Ordinal))
        {
            RedrawScheduleGraphs();
        }
    }

    private void ScheduleCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RedrawScheduleGraphs();
    }

    private void KelvinScheduleCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        BeginDrag(sender as Canvas, ScheduleSeries.Kelvin, e.GetPosition((IInputElement)sender));
        e.Handled = true;
    }

    private void LuxScheduleCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        BeginDrag(sender as Canvas, ScheduleSeries.Lux, e.GetPosition((IInputElement)sender));
        e.Handled = true;
    }

    private void KelvinScheduleCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isKelvinDragging || sender is not Canvas canvas || _dragKelvinHour < 0)
            return;

        ApplyDraggedValue(canvas, ScheduleSeries.Kelvin, _dragKelvinHour, e.GetPosition(canvas).Y);
        e.Handled = true;
    }

    private void LuxScheduleCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isLuxDragging || sender is not Canvas canvas || _dragLuxHour < 0)
            return;

        ApplyDraggedValue(canvas, ScheduleSeries.Lux, _dragLuxHour, e.GetPosition(canvas).Y);
        e.Handled = true;
    }

    private void ScheduleCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndDrag(sender as Canvas);
        e.Handled = true;
    }

    private void ScheduleCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            EndDrag(sender as Canvas);
    }

    private void BeginDrag(Canvas? canvas, ScheduleSeries series, Point position)
    {
        if (canvas == null || _vm.TunableWhiteHourPoints.Count == 0)
            return;

        var hour = ResolveHour(canvas, position.X);
        if (series == ScheduleSeries.Kelvin)
        {
            _isKelvinDragging = true;
            _dragKelvinHour = hour;
        }
        else
        {
            _isLuxDragging = true;
            _dragLuxHour = hour;
        }

        canvas.CaptureMouse();
        ApplyDraggedValue(canvas, series, hour, position.Y);
    }

    private void EndDrag(Canvas? canvas)
    {
        if (canvas?.IsMouseCaptured == true)
            canvas.ReleaseMouseCapture();

        _isKelvinDragging = false;
        _isLuxDragging = false;
        _dragKelvinHour = -1;
        _dragLuxHour = -1;
    }

    private void ApplyDraggedValue(Canvas canvas, ScheduleSeries series, int hour, double mouseY)
    {
        var point = _vm.TunableWhiteHourPoints.FirstOrDefault(p => p.Hour == hour);
        if (point == null)
            return;

        if (series == ScheduleSeries.Kelvin)
        {
            point.Kelvin = MapMouseToValue(mouseY, canvas.ActualHeight, MinKelvin, MaxKelvin, KelvinStep, useAbsoluteSteps: false);
        }
        else
        {
            point.Lux = MapMouseToValue(mouseY, canvas.ActualHeight, MinLux, MaxLux, LuxStep, useAbsoluteSteps: false);
        }
    }

    private static int ResolveHour(Canvas canvas, double mouseX)
    {
        var plotWidth = Math.Max(1d, canvas.ActualWidth - PlotLeft - PlotRight);
        var x = Math.Clamp(mouseX, PlotLeft, PlotLeft + plotWidth);
        var normalized = (x - PlotLeft) / plotWidth;
        return Math.Clamp((int)Math.Round(normalized * 23d), 0, 23);
    }

    private static int MapMouseToValue(double mouseY, double canvasHeight, int min, int max, int step, bool useAbsoluteSteps)
    {
        var plotHeight = Math.Max(1d, canvasHeight - PlotTop - PlotBottom);
        var y = Math.Clamp(mouseY, PlotTop, PlotTop + plotHeight);
        var ratio = 1d - ((y - PlotTop) / plotHeight);
        var raw = min + (ratio * (max - min));

        int snapped;
        if (useAbsoluteSteps)
        {
            snapped = (int)Math.Round(raw / step) * step;
        }
        else
        {
            snapped = min + ((int)Math.Round((raw - min) / step) * step);
        }

        return Math.Clamp(snapped, min, max);
    }

    private void RedrawScheduleGraphs()
    {
        DrawGraph(KelvinScheduleCanvas, ScheduleSeries.Kelvin);
        DrawGraph(LuxScheduleCanvas, ScheduleSeries.Lux);
    }

    private void DrawGraph(Canvas? canvas, ScheduleSeries series)
    {
        if (canvas == null)
            return;

        canvas.Children.Clear();
        if (canvas.ActualWidth < 80 || canvas.ActualHeight < 80)
            return;

        var points = _vm.TunableWhiteHourPoints.OrderBy(p => p.Hour).ToList();
        if (points.Count == 0)
            return;

        var min = series == ScheduleSeries.Kelvin ? MinKelvin : MinLux;
        var max = series == ScheduleSeries.Kelvin ? MaxKelvin : MaxLux;
        var stroke = series == ScheduleSeries.Kelvin
            ? new SolidColorBrush(Color.FromRgb(193, 118, 62))
            : new SolidColorBrush(Color.FromRgb(65, 127, 191));

        var plotWidth = Math.Max(1d, canvas.ActualWidth - PlotLeft - PlotRight);
        var plotHeight = Math.Max(1d, canvas.ActualHeight - PlotTop - PlotBottom);

        DrawSelectionFade(canvas, points, series, min, max, plotWidth, plotHeight);
        DrawGrid(canvas, plotWidth, plotHeight);
        DrawAxisLabels(canvas, min, max, plotWidth, plotHeight);

        var polyline = new Polyline
        {
            Stroke = stroke,
            StrokeThickness = 2d,
            IsHitTestVisible = false
        };
        canvas.Children.Add(polyline);

        foreach (var point in points)
        {
            var value = series == ScheduleSeries.Kelvin ? point.Kelvin : point.Lux;
            var x = PlotLeft + (point.Hour * plotWidth / 23d);
            var y = ValueToY(value, min, max, plotHeight);

            polyline.Points.Add(new Point(x, y));

            var marker = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = series == ScheduleSeries.Kelvin ? point.KelvinBrush : point.LuxBrush,
                Stroke = Brushes.White,
                StrokeThickness = 1.2,
                IsHitTestVisible = false,
                ToolTip = $"{point.HourLabel}: {value}{(series == ScheduleSeries.Kelvin ? " K" : " lx")}"
            };
            Canvas.SetLeft(marker, x - 5);
            Canvas.SetTop(marker, y - 5);
            canvas.Children.Add(marker);

            AddPointValueLabel(canvas, point.Hour, x, y, value, plotWidth, plotHeight);
        }
    }

    private static void DrawSelectionFade(
        Canvas canvas,
        IReadOnlyList<TunableWhiteHourPointViewModel> points,
        ScheduleSeries series,
        int min,
        int max,
        double plotWidth,
        double plotHeight)
    {
        if (points.Count == 0)
            return;

        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            MappingMode = BrushMappingMode.RelativeToBoundingBox
        };

        foreach (var point in points)
        {
            var baseColor = ResolvePointColor(point, series);
            gradient.GradientStops.Add(
                new GradientStop(Color.FromArgb(84, baseColor.R, baseColor.G, baseColor.B), point.Hour / 23d));
        }

        // Fill the whole plot area with value-based horizontal fade.
        canvas.Children.Add(new Rectangle
        {
            Width = plotWidth,
            Height = plotHeight,
            Fill = gradient,
            IsHitTestVisible = false
        });
        Canvas.SetLeft(canvas.Children[^1], PlotLeft);
        Canvas.SetTop(canvas.Children[^1], PlotTop);
    }

    private static void DrawGrid(Canvas canvas, double plotWidth, double plotHeight)
    {
        var gridBrush = ThemeBrush("BorderBrush", Color.FromRgb(220, 220, 220));
        for (var i = 0; i <= 4; i++)
        {
            var y = PlotTop + ((plotHeight / 4d) * i);
            canvas.Children.Add(new Line
            {
                X1 = PlotLeft,
                X2 = PlotLeft + plotWidth,
                Y1 = y,
                Y2 = y,
                Stroke = gridBrush,
                StrokeThickness = i == 4 ? 1.2 : 0.8,
                IsHitTestVisible = false
            });
        }

        var guideHours = new[] { 0, 3, 6, 9, 12, 15, 18, 21, 23 };
        foreach (var hour in guideHours)
        {
            var x = PlotLeft + (hour * plotWidth / 23d);
            canvas.Children.Add(new Line
            {
                X1 = x,
                X2 = x,
                Y1 = PlotTop,
                Y2 = PlotTop + plotHeight,
                Stroke = gridBrush,
                StrokeThickness = 0.6,
                IsHitTestVisible = false
            });
        }
    }

    private static void DrawAxisLabels(Canvas canvas, int min, int max, double plotWidth, double plotHeight)
    {
        AddLabel(canvas, max.ToString(), 0, PlotTop - 6);
        AddLabel(canvas, min.ToString(), 0, PlotTop + plotHeight - 8);

        foreach (var hour in new[] { 0, 6, 12, 18, 23 })
        {
            var x = PlotLeft + (hour * plotWidth / 23d) - 8;
            AddLabel(canvas, hour.ToString("00"), x, PlotTop + plotHeight + 2);
        }
    }

    private static void AddLabel(Canvas canvas, string text, double x, double y)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            FontSize = 10,
            Foreground = ThemeBrush("MutedBrush", Color.FromRgb(90, 90, 90)),
            IsHitTestVisible = false
        };

        Canvas.SetLeft(textBlock, x);
        Canvas.SetTop(textBlock, y);
        canvas.Children.Add(textBlock);
    }

    private static void AddPointValueLabel(Canvas canvas, int hour, double x, double y, int value, double plotWidth, double plotHeight)
    {
        var text = new TextBlock
        {
            Text = value.ToString(CultureInfo.InvariantCulture),
            FontSize = 9,
            Foreground = ThemeBrush("TextBrush", Color.FromRgb(48, 48, 48)),
            IsHitTestVisible = false
        };

        var holder = new Border
        {
            Background = ThemeAlphaBrush("CardBrush", IsDarkTheme() ? (byte)232 : (byte)235, Color.FromRgb(255, 255, 255)),
            BorderBrush = ThemeAlphaBrush("BorderBrush", IsDarkTheme() ? (byte)220 : (byte)160, Color.FromRgb(210, 210, 210)),
            BorderThickness = new Thickness(0.6),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(2, 0, 2, 0),
            Child = text,
            IsHitTestVisible = false
        };

        holder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = holder.DesiredSize;

        var preferredTop = (hour % 2 == 0) ? y - size.Height - 5 : y + 5;
        var minTop = PlotTop;
        var maxTop = PlotTop + plotHeight - size.Height;
        var top = Math.Clamp(preferredTop, minTop, Math.Max(minTop, maxTop));

        var minLeft = PlotLeft;
        var maxLeft = PlotLeft + plotWidth - size.Width;
        var left = Math.Clamp(x - (size.Width / 2d), minLeft, Math.Max(minLeft, maxLeft));

        Canvas.SetLeft(holder, left);
        Canvas.SetTop(holder, top);
        canvas.Children.Add(holder);
    }

    private static Brush ThemeBrush(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is Brush brush)
            return brush;

        return new SolidColorBrush(fallback);
    }

    private static SolidColorBrush ThemeAlphaBrush(string key, byte alpha, Color fallback)
    {
        var color = fallback;
        if (Application.Current?.TryFindResource(key) is SolidColorBrush brush)
            color = brush.Color;

        return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
    }

    private static bool IsDarkTheme() =>
        string.Equals(App.CurrentTheme, "Dark", StringComparison.OrdinalIgnoreCase);

    private static Color ResolvePointColor(TunableWhiteHourPointViewModel point, ScheduleSeries series)
    {
        var brush = series == ScheduleSeries.Kelvin ? point.KelvinBrush : point.LuxBrush;
        return (brush as SolidColorBrush)?.Color ?? Colors.SlateGray;
    }

    private static double ValueToY(int value, int min, int max, double plotHeight)
    {
        var clamped = Math.Clamp(value, min, max);
        var span = Math.Max(1, max - min);
        var normalized = (clamped - min) / (double)span;
        return PlotTop + ((1d - normalized) * plotHeight);
    }

    private enum ScheduleSeries
    {
        Kelvin,
        Lux
    }
}
