using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AccessAppMqttWpf.Converters
{
    public sealed class StatusToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = (value?.ToString() ?? "").Trim().ToLowerInvariant();

            Brush Good() => (Brush?)Application.Current.TryFindResource("GoodBrush") ?? Brushes.LimeGreen;
            Brush Bad()  => (Brush?)Application.Current.TryFindResource("BadBrush")  ?? Brushes.IndianRed;
            Brush Warn() => (Brush?)Application.Current.TryFindResource("WarnBrush") ?? Brushes.Goldenrod;
            Brush Muted() => (Brush?)Application.Current.TryFindResource("MutedBrush") ?? Brushes.Gray;

            // IMPORTANT: queued/progress state must override "success" (some lines contain both words).
            // Use AccentBrush (blue) as the queued color.
            Brush Queued() => (Brush?)Application.Current.TryFindResource("AccentBrush") ?? Brushes.DodgerBlue;

            if (s.Contains("queued") || s.Contains("queue") || s.Contains("programming") || s.Contains("upgrading") || s.Contains("running"))
                return Queued();

            if (s.Contains("success") || s == "ok" || s.Contains("achieved"))
                return Good();

            if (s.Contains("fail") || s.Contains("error"))
                return Bad();

            if (s.Contains("warn"))
                return Warn();

            return Muted();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
