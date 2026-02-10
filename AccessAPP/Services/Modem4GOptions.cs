namespace AccessAPP.Services;

public sealed class Modem4GOptions
{
    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "192.168.0.1";
    public string Password { get; set; } = "Kode1234!";
    public int PollIntervalSeconds { get; set; } = 30;
    public int TimeoutSeconds { get; set; } = 5;
}

public sealed class Modem4GSnapshot
{
    public DateTimeOffset PolledAtUtc { get; set; }
    public string State { get; set; } = "";
    public string NetworkType { get; set; } = "";
    public int? SignalBar { get; set; }
    public int? RssiDbm { get; set; }
    public int? LteRsrpDbm { get; set; }
    public int? LteRsrqDb { get; set; }
    public int? LteSnrDb { get; set; }
    public string Provider { get; set; } = "";
}
