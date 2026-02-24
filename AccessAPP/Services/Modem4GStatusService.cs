using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AccessAPP.Services;

public sealed class Modem4GStatusService : IDisposable
{
    private static readonly string[] StatusFields =
    {
        "wan_connect_status",
        "network_type",
        "signalbar",
        "network_provider",
        "network_provider_fullname",
        "lte_rsrp",
        "lte_rsrq",
        "lte_rssi",
        "lte_snr",
        "rssi"
    };

    private readonly Modem4GOptions _options;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Modem4GSnapshot? _lastSnapshot;
    private DateTimeOffset _lastPollUtc = DateTimeOffset.MinValue;

    public Modem4GStatusService(IConfiguration configuration)
    {
        _options = configuration.GetSection("Modem4G").Get<Modem4GOptions>() ?? new Modem4GOptions();
        _options.Host = string.IsNullOrWhiteSpace(_options.Host) ? "192.168.0.1" : _options.Host.Trim();
        _options.PollIntervalSeconds = Math.Max(5, _options.PollIntervalSeconds);
        _options.TimeoutSeconds = Math.Max(2, _options.TimeoutSeconds);

        var host = _options.Host.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || _options.Host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? _options.Host
            : $"http://{_options.Host}";
        host = host.TrimEnd('/');

        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri($"{host}/"),
            Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds)
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        _http.DefaultRequestHeaders.Referrer = new Uri($"{host}/index.html");
    }

    public async Task<Modem4GSnapshot?> GetLatestAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
            return null;

        var now = DateTimeOffset.UtcNow;
        if (_lastSnapshot != null && (now - _lastPollUtc) < TimeSpan.FromSeconds(_options.PollIntervalSeconds))
            return _lastSnapshot;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_lastSnapshot != null && (now - _lastPollUtc) < TimeSpan.FromSeconds(_options.PollIntervalSeconds))
                return _lastSnapshot;

            await EnsureLoggedInAsync(ct).ConfigureAwait(false);
            var values = await QueryStatusAsync(ct).ConfigureAwait(false);

            _lastSnapshot = BuildSnapshot(values, now);
            _lastPollUtc = now;
            return _lastSnapshot;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _lastPollUtc = DateTimeOffset.UtcNow;
            AppLog.Verbose($"4G modem poll failed: {ex.Message}");
            return _lastSnapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoggedInAsync(CancellationToken ct)
    {
        var payload = new Dictionary<string, string>
        {
            ["isTest"] = "false",
            ["goformId"] = "LOGIN",
            ["password"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(_options.Password ?? string.Empty))
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "goform/goform_set_cmd_process")
        {
            Content = new FormUrlEncodedContent(payload)
        };
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        _ = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, string>> QueryStatusAsync(CancellationToken ct)
    {
        var cmd = string.Join(",", StatusFields);
        var url = $"goform/goform_get_cmd_process?isTest=false&multi_data=1&cmd={Uri.EscapeDataString(cmd)}";
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return map;

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            switch (prop.Value.ValueKind)
            {
                case JsonValueKind.String:
                    map[prop.Name] = prop.Value.GetString() ?? "";
                    break;
                case JsonValueKind.Number:
                    map[prop.Name] = prop.Value.ToString();
                    break;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    map[prop.Name] = prop.Value.GetBoolean() ? "1" : "0";
                    break;
            }
        }

        return map;
    }

    private static Modem4GSnapshot BuildSnapshot(Dictionary<string, string> map, DateTimeOffset ts)
    {
        var networkType = GetValue(map, "network_type");
        var provider = GetValue(map, "network_provider_fullname");
        if (string.IsNullOrWhiteSpace(provider))
            provider = GetValue(map, "network_provider");

        var wan = GetValue(map, "wan_connect_status");
        var state = "unknown";
        if (!string.IsNullOrWhiteSpace(wan))
        {
            var wanLower = wan.ToLowerInvariant();
            if (wanLower.Contains("connect"))
                state = "connected";
            else if (wanLower.Contains("disconnect"))
                state = "disconnected";
        }

        var rssi = ParseInt(GetValue(map, "lte_rssi"));
        if (rssi is null)
            rssi = ParseInt(GetValue(map, "rssi"));

        return new Modem4GSnapshot
        {
            PolledAtUtc = ts,
            State = state,
            NetworkType = networkType,
            SignalBar = ParseInt(GetValue(map, "signalbar")),
            RssiDbm = rssi,
            LteRsrpDbm = ParseInt(GetValue(map, "lte_rsrp")),
            LteRsrqDb = ParseInt(GetValue(map, "lte_rsrq")),
            LteSnrDb = ParseInt(GetValue(map, "lte_snr")),
            Provider = provider
        };
    }

    private static string GetValue(Dictionary<string, string> map, string key)
        => map.TryGetValue(key, out var value) ? (value ?? "") : "";

    private static int? ParseInt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (int.TryParse(raw.Trim(), out var direct))
            return direct;

        var m = Regex.Match(raw, @"-?\d+");
        if (m.Success && int.TryParse(m.Value, out var extracted))
            return extracted;

        return null;
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
