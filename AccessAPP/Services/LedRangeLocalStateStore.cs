using System.Text.Json;
using AccessAPP.Models;

namespace AccessAPP.Services
{
    public sealed class LedRangeLocalStateStore
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, LedRangeDeviceRow> _connected = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LedRangeDeviceRow> _failed = new(StringComparer.OrdinalIgnoreCase);
        private LedRangeStateSnapshot _snapshot = new();

        public LedRangeStateSnapshot GetSnapshot()
        {
            lock (_lock)
            {
                return new LedRangeStateSnapshot
                {
                    StatusText = _snapshot.StatusText,
                    ProgressText = _snapshot.ProgressText,
                    RequestedTotal = _snapshot.RequestedTotal,
                    TriedCount = _snapshot.TriedCount,
                    ConnectedCount = _snapshot.ConnectedCount,
                    FailedCount = _snapshot.FailedCount,
                    ProgressPercent = _snapshot.ProgressPercent,
                    RequestId = _snapshot.RequestId,
                    Stage = _snapshot.Stage,
                    MinRssi = _snapshot.MinRssi,
                    LastUpdatedUtc = _snapshot.LastUpdatedUtc,
                    ConnectedDevices = _connected.Values
                        .OrderBy(x => x.Mac, StringComparer.OrdinalIgnoreCase)
                        .Select(CloneRow)
                        .ToList(),
                    FailedDevices = _failed.Values
                        .OrderBy(x => x.Mac, StringComparer.OrdinalIgnoreCase)
                        .Select(CloneRow)
                        .ToList()
                };
            }
        }

        public List<string> GetFailedMacs()
        {
            lock (_lock)
            {
                return _failed.Keys
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        public void ResetForStart(string requestId, int minRssi)
        {
            lock (_lock)
            {
                _connected.Clear();
                _failed.Clear();
                _snapshot = new LedRangeStateSnapshot
                {
                    StatusText = "Starting...",
                    ProgressText = "0 / 0 tried",
                    RequestedTotal = 0,
                    TriedCount = 0,
                    ConnectedCount = 0,
                    FailedCount = 0,
                    ProgressPercent = 0,
                    RequestId = requestId ?? "",
                    Stage = "starting",
                    MinRssi = minRssi,
                    LastUpdatedUtc = DateTimeOffset.UtcNow,
                    ConnectedDevices = new List<LedRangeDeviceRow>(),
                    FailedDevices = new List<LedRangeDeviceRow>()
                };
            }
        }

        public void PrepareForRetry(int requestedTotal)
        {
            lock (_lock)
            {
                _snapshot.TriedCount = 0;
                _snapshot.ConnectedCount = 0;
                _snapshot.FailedCount = 0;
                _snapshot.RequestedTotal = requestedTotal;
                _snapshot.ProgressPercent = requestedTotal > 0 ? 0 : 100;
                _snapshot.ProgressText = $"0 / {requestedTotal} tried";
                _snapshot.StatusText = $"Retrying {requestedTotal} failed devices...";
                _snapshot.Stage = "retry-started";
                _snapshot.LastUpdatedUtc = DateTimeOffset.UtcNow;
            }
        }

        public void SetStatusText(string text)
        {
            lock (_lock)
            {
                _snapshot.StatusText = text ?? "";
                _snapshot.LastUpdatedUtc = DateTimeOffset.UtcNow;
            }
        }

        public void ApplyStage(object payload)
        {
            if (payload == null) return;

            JsonDocument? doc = null;
            try
            {
                var json = JsonSerializer.Serialize(payload);
                doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var stage = GetString(root, "stage");
                var mac = GetString(root, "mac");
                var model = GetString(root, "model");
                var color = GetString(root, "color");
                var error = GetString(root, "error");
                var requestId = GetString(root, "requestId");
                var minRssi = GetInt(root, "minRssi", null);
                var rssi = GetInt(root, "rssi", 0);
                var chip = GetInt(root, "chip", 0);
                var forceAll = GetBool(root, "forceAll", false);
                var disconnected = GetInt(root, "disconnected", 0);

                lock (_lock)
                {
                    if (!string.IsNullOrWhiteSpace(requestId))
                        _snapshot.RequestId = requestId;

                    if (minRssi.HasValue)
                        _snapshot.MinRssi = minRssi.Value;

                    _snapshot.Stage = stage ?? "";

                    if (string.Equals(stage, "connected", StringComparison.OrdinalIgnoreCase))
                    {
                        Upsert(_connected, mac, model, rssi, chip, color, "connected", "");
                        Remove(_failed, mac);
                        _snapshot.StatusText = $"Local: connected {mac} ({color}, RSSI {rssi})";
                    }
                    else if (string.Equals(stage, "failed", StringComparison.OrdinalIgnoreCase))
                    {
                        Upsert(_failed, mac, model, rssi, chip, color, "failed", error);
                        Remove(_connected, mac);
                        _snapshot.StatusText = $"Local: failed {mac} ({error})";
                    }
                    else if (string.Equals(stage, "disconnected", StringComparison.OrdinalIgnoreCase))
                    {
                        Remove(_connected, mac);
                        _snapshot.StatusText = $"Local: disconnected {mac}";
                    }
                    else if (string.Equals(stage, "disconnect-failed", StringComparison.OrdinalIgnoreCase))
                    {
                        Upsert(_failed, mac, model, rssi, chip, color, "disconnect-failed", error);
                        _snapshot.StatusText = $"Local: disconnect failed {mac} ({error})";
                    }
                    else if (string.Equals(stage, "started", StringComparison.OrdinalIgnoreCase))
                    {
                        var requested = GetInt(root, "requested", 0);
                        _snapshot.RequestedTotal = requested;
                        _snapshot.TriedCount = 0;
                        _snapshot.ConnectedCount = 0;
                        _snapshot.FailedCount = 0;
                        _snapshot.ProgressPercent = requested > 0 ? 0 : 100;
                        _snapshot.ProgressText = $"0 / {requested} tried";
                        _snapshot.StatusText = $"Local: started, requested {requested}, min RSSI {_snapshot.MinRssi}";
                    }
                    else if (string.Equals(stage, "completed", StringComparison.OrdinalIgnoreCase))
                    {
                        var requested = GetInt(root, "requested", _snapshot.RequestedTotal);
                        var tried = GetInt(root, "tried", _snapshot.TriedCount);
                        var connected = GetInt(root, "connected", _snapshot.ConnectedCount);
                        var failed = GetInt(root, "failed", _snapshot.FailedCount);
                        _snapshot.RequestedTotal = requested;
                        _snapshot.TriedCount = tried;
                        _snapshot.ConnectedCount = connected;
                        _snapshot.FailedCount = failed;
                        _snapshot.ProgressPercent = requested > 0 ? Math.Clamp((100.0 * tried) / requested, 0, 100) : 100;
                        _snapshot.ProgressText = $"{tried} / {requested} tried";
                        _snapshot.StatusText = $"Local: completed. Connected {connected}, failed {failed}";
                    }
                    else if (string.Equals(stage, "canceled", StringComparison.OrdinalIgnoreCase))
                    {
                        var requested = GetInt(root, "requested", _snapshot.RequestedTotal);
                        var tried = GetInt(root, "tried", _snapshot.TriedCount);
                        var connected = GetInt(root, "connected", _snapshot.ConnectedCount);
                        var failed = GetInt(root, "failed", _snapshot.FailedCount);
                        _snapshot.RequestedTotal = requested;
                        _snapshot.TriedCount = tried;
                        _snapshot.ConnectedCount = connected;
                        _snapshot.FailedCount = failed;
                        _snapshot.ProgressPercent = requested > 0 ? Math.Clamp((100.0 * tried) / requested, 0, 100) : 100;
                        _snapshot.ProgressText = $"{tried} / {requested} tried";
                        _snapshot.StatusText = $"Local: canceled. Tried {tried}/{requested}.";
                    }
                    else if (string.Equals(stage, "disconnect-completed", StringComparison.OrdinalIgnoreCase))
                    {
                        var failed = GetInt(root, "failed", 0);
                        _snapshot.StatusText = forceAll
                            ? $"Local: force disconnect completed. Disconnected {disconnected}, failed {failed}"
                            : $"Local: disconnect completed. Disconnected {disconnected}, failed {failed}";
                    }
                    else if (string.Equals(stage, "retry-round-started", StringComparison.OrdinalIgnoreCase))
                    {
                        var remaining = GetInt(root, "remaining", 0);
                        _snapshot.StatusText = $"Local: retry round started. Remaining {remaining}";
                    }
                    else if (string.Equals(stage, "retry-round-completed", StringComparison.OrdinalIgnoreCase))
                    {
                        var tried = GetInt(root, "tried", _snapshot.TriedCount);
                        var connected = GetInt(root, "connected", _snapshot.ConnectedCount);
                        var failed = GetInt(root, "failed", _snapshot.FailedCount);
                        _snapshot.StatusText = $"Local: retry round completed. Connected {connected}, failed {failed}";
                        _snapshot.TriedCount = tried;
                        _snapshot.ConnectedCount = connected;
                        _snapshot.FailedCount = failed;
                    }

                    var requestedFromStage = GetInt(root, "requested", _snapshot.RequestedTotal);
                    var triedFromStage = GetInt(root, "tried", _snapshot.TriedCount);
                    var connectedFromStage = GetInt(root, "connected", _snapshot.ConnectedCount);
                    var failedFromStage = GetInt(root, "failed", _snapshot.FailedCount);

                    _snapshot.RequestedTotal = requestedFromStage;
                    _snapshot.TriedCount = triedFromStage;
                    _snapshot.ConnectedCount = connectedFromStage;
                    _snapshot.FailedCount = failedFromStage;
                    _snapshot.ProgressPercent = requestedFromStage > 0
                        ? Math.Clamp((100.0 * triedFromStage) / requestedFromStage, 0, 100)
                        : 100;
                    _snapshot.ProgressText = $"{triedFromStage} / {requestedFromStage} tried";
                    _snapshot.LastUpdatedUtc = DateTimeOffset.UtcNow;
                }
            }
            finally
            {
                doc?.Dispose();
            }
        }

        private static LedRangeDeviceRow CloneRow(LedRangeDeviceRow row) => new()
        {
            Mac = row.Mac,
            Model = row.Model,
            Rssi = row.Rssi,
            Chip = row.Chip,
            Color = row.Color,
            Status = row.Status,
            Error = row.Error
        };

        private static void Upsert(
            Dictionary<string, LedRangeDeviceRow> dict,
            string mac,
            string model,
            int rssi,
            int chip,
            string color,
            string status,
            string error)
        {
            if (string.IsNullOrWhiteSpace(mac)) return;
            if (!dict.TryGetValue(mac, out var row))
            {
                row = new LedRangeDeviceRow { Mac = mac };
                dict[mac] = row;
            }

            row.Model = model ?? "";
            row.Rssi = rssi;
            row.Chip = chip;
            row.Color = color ?? "";
            row.Status = status ?? "";
            row.Error = error ?? "";
        }

        private static void Remove(Dictionary<string, LedRangeDeviceRow> dict, string mac)
        {
            if (string.IsNullOrWhiteSpace(mac)) return;
            dict.Remove(mac);
        }

        private static string GetString(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var el)) return "";
            return el.ValueKind == JsonValueKind.String ? (el.GetString() ?? "") : el.ToString();
        }

        private static int GetInt(JsonElement root, string name, int fallback)
        {
            if (!root.TryGetProperty(name, out var el)) return fallback;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
            if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var s)) return s;
            return fallback;
        }

        private static int? GetInt(JsonElement root, string name, int? fallback)
        {
            if (!root.TryGetProperty(name, out var el)) return fallback;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
            if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var s)) return s;
            return fallback;
        }

        private static bool GetBool(JsonElement root, string name, bool fallback)
        {
            if (!root.TryGetProperty(name, out var el)) return fallback;
            if (el.ValueKind == JsonValueKind.True) return true;
            if (el.ValueKind == JsonValueKind.False) return false;
            if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var b)) return b;
            return fallback;
        }
    }
}
