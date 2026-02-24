namespace AccessAPP.Models
{
    public sealed class LedRangeDeviceRow
    {
        public string Mac { get; set; } = "";
        public string Model { get; set; } = "";
        public int Rssi { get; set; }
        public int Chip { get; set; }
        public string Color { get; set; } = "";
        public string Status { get; set; } = "";
        public string Error { get; set; } = "";
    }

    public sealed class LedRangeStateSnapshot
    {
        public string StatusText { get; set; } = "Idle";
        public string ProgressText { get; set; } = "0 / 0 tried";
        public int RequestedTotal { get; set; }
        public int TriedCount { get; set; }
        public int ConnectedCount { get; set; }
        public int FailedCount { get; set; }
        public double ProgressPercent { get; set; }
        public string RequestId { get; set; } = "";
        public string Stage { get; set; } = "";
        public int MinRssi { get; set; } = -75;
        public DateTimeOffset LastUpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
        public List<LedRangeDeviceRow> ConnectedDevices { get; set; } = new();
        public List<LedRangeDeviceRow> FailedDevices { get; set; } = new();
    }

    public sealed class LedRangeStartRequest
    {
        public int MinRssi { get; set; } = -75;
        public string Model { get; set; } = "All";
        public int MaxConnectAttempts { get; set; } = 3;
        public string Pincode { get; set; } = "";
        public bool UseBothChips { get; set; } = true;
    }

    public sealed class LedRangeDisconnectRequest
    {
        public bool ForceAll { get; set; }
    }
}
