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

            Brush FromKey(string key, Brush fallback)
                => (Brush?)Application.Current.TryFindResource(key) ?? fallback;

            var good = FromKey("GoodBrush", Brushes.LimeGreen);
            var bad = FromKey("BadBrush", Brushes.IndianRed);
            var warn = FromKey("WarnBrush", Brushes.Orange);
            var noFw = FromKey("NoFwBrush", Brushes.DeepSkyBlue);
            var muted = FromKey("MutedBrush", Brushes.Gray);
            var accent = FromKey("AccentBrush", Brushes.DodgerBlue);

            // ---- IMPORTANT ORDERING ----
            // Queued/Programming must override "Success" (some statuses may contain both words)
            if (s.Contains("queued") || s.Contains("queue") || s.Contains("programming") || s.Contains("upgrading"))
                return accent;

            if (s.Contains("requested update") || s.Contains("requested"))
                return accent;

            if (s.Contains("nofwread") || s.Contains("no fw"))
                return noFw;

            if (s.Contains("done") || s.Contains("success") || s.Contains("ok") || s.Contains("completed"))
                return good;

            if (s.Contains("fail") || s.Contains("error"))
                return bad;

            if (s.Contains("warn"))
                return warn;

            return muted;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
