using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AccessAPP.Services;

public sealed class CassiaWebSettingsService
{
    private const string DefaultEncryptedPassword = "U2FsdGVkX19Z8zcPoszwmXgWFuCY0jVVtUgDtisi/d0=";

    private static readonly string[] SaveTemplatePaths =
    {
        "name",
        "fat",
        "wireless.country",
        "wireless.mode",
        "ble_power",
        "ac.force_network",
        "oauth-enable",
        "ssh-login",
        "wired.proto",
        "wired.dns1",
        "wired.dns2",
        "wireless_ap.disabled",
        "wireless_ap.ssid",
        "wireless_ap.ca_hidden",
        "wireless_ap.password",
        "wireless_ap.ip",
        "wireless_ap.netmask",
        "dongle",
        "wireless_verify",
        "bt_antenna.chip0",
        "bt_antenna.chip1"
    };

    private static readonly Regex CsrfJsonRegex = new(
        "\"csrf\"\\s*:\\s*\"(?<value>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CsrfInputRegex = new(
        "name\\s*=\\s*['\"]csrf['\"][^>]*value\\s*=\\s*['\"](?<value>[^'\"]+)['\"]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly string _defaultGatewayAddress;
    private readonly string _defaultUsername;
    private readonly string _defaultPassword;
    private readonly string _defaultPasswordEncrypted;
    private readonly TimeSpan _timeout;
    private readonly int _defaultPort;

    public CassiaWebSettingsService(IConfiguration configuration)
    {
        _defaultGatewayAddress =
            configuration.GetValue<string>("CassiaWebUi:GatewayIp")
            ?? configuration.GetValue<string>("GatewayConfiguration:IpAddress")
            ?? "192.168.40.1";

        _defaultUsername = configuration.GetValue<string>("CassiaWebUi:Username") ?? "admin";
        _defaultPassword =
            configuration.GetValue<string>("CassiaWebUi:Password")
            ?? configuration.GetValue<string>("Modem4G:Password")
            ?? "Kode1234!";
        _defaultPasswordEncrypted =
            configuration.GetValue<string>("CassiaWebUi:PasswordEncrypted")
            ?? DefaultEncryptedPassword;

        _defaultPort = configuration.GetValue<int?>("GatewayConfiguration:Port") ?? 80;

        var timeoutSec = configuration.GetValue<int?>("CassiaWebUi:TimeoutSeconds") ?? 8;
        _timeout = TimeSpan.FromSeconds(Math.Max(2, timeoutSec));
    }

    public async Task<CassiaWebSettingsSnapshot> GetSettingsAsync(CassiaWebSettingsRequest request, CancellationToken ct)
    {
        await using var session = await CreateAuthenticatedSessionAsync(request, ct).ConfigureAwait(false);

        var infoObject = ParseInfoObject(session.InitialInfoJson);
        var csrf = await ResolveCsrfAsync(session, infoObject, ct).ConfigureAwait(false);
        var template = BuildSavePayloadTemplate(infoObject, csrf);

        return new CassiaWebSettingsSnapshot(
            GatewayIp: session.BaseUri.Host,
            Csrf: csrf,
            Settings: infoObject,
            SavePayloadTemplate: template);
    }

    public async Task<CassiaWebSettingsSnapshot> SaveSettingsAsync(
        CassiaWebSettingsRequest request,
        JsonObject settingsPayload,
        CancellationToken ct)
    {
        if (settingsPayload == null || settingsPayload.Count == 0)
            throw new ArgumentException("settings payload is empty.", nameof(settingsPayload));

        await using var session = await CreateAuthenticatedSessionAsync(request, ct).ConfigureAwait(false);

        var infoObject = ParseInfoObject(session.InitialInfoJson);
        var csrf = await ResolveCsrfAsync(session, infoObject, ct).ConfigureAwait(false);

        var payload = (settingsPayload.DeepClone() as JsonObject) ?? new JsonObject();
        if (!payload.ContainsKey("csrf") && !string.IsNullOrWhiteSpace(csrf))
            payload["csrf"] = csrf;

        await PostInfoAsync(session.Client, payload, session.BaseUri, ct).ConfigureAwait(false);

        var refreshedJson = await GetInfoJsonAsync(session.Client, session.BaseUri, ct).ConfigureAwait(false);
        var refreshedObject = ParseInfoObject(refreshedJson);
        var refreshedCsrf = await ResolveCsrfAsync(session, refreshedObject, ct).ConfigureAwait(false);
        var refreshedTemplate = BuildSavePayloadTemplate(refreshedObject, refreshedCsrf);

        return new CassiaWebSettingsSnapshot(
            GatewayIp: session.BaseUri.Host,
            Csrf: refreshedCsrf,
            Settings: refreshedObject,
            SavePayloadTemplate: refreshedTemplate);
    }

    private async Task<AuthenticatedSession> CreateAuthenticatedSessionAsync(CassiaWebSettingsRequest request, CancellationToken ct)
    {
        var gatewayAddress = ResolveGatewayAddress(request.GatewayIp);
        var username = string.IsNullOrWhiteSpace(request.Username) ? _defaultUsername : request.Username!.Trim();
        var candidates = BuildPasswordCandidates(request).ToList();
        var errors = new List<string>();

        foreach (var passwordCandidate in candidates)
        {
            var session = CreateSession(gatewayAddress);
            try
            {
                await LoginAsync(session.Client, session.BaseUri, username, passwordCandidate, ct).ConfigureAwait(false);

                var infoJson = await GetInfoJsonAsync(session.Client, session.BaseUri, ct).ConfigureAwait(false);
                session.InitialInfoJson = infoJson;
                return session;
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }

        var errorText = errors.Count == 0 ? "Authentication failed." : string.Join(" | ", errors.Distinct(StringComparer.OrdinalIgnoreCase));
        throw new InvalidOperationException($"Unable to authenticate against Cassia web interface ({gatewayAddress}). {errorText}");
    }

    private AuthenticatedSession CreateSession(string gatewayAddress)
    {
        var baseUri = BuildBaseUri(gatewayAddress);
        var cookies = new CookieContainer();
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = cookies,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        var client = new HttpClient(handler)
        {
            BaseAddress = baseUri,
            Timeout = _timeout
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        client.DefaultRequestHeaders.Referrer = new Uri(baseUri, "cassia/login");

        return new AuthenticatedSession(client, baseUri, cookies);
    }

    private static async Task LoginAsync(HttpClient client, Uri baseUri, string username, string passwordToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "cassia/login")
        {
            Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("username", username),
                new KeyValuePair<string, string>("password", passwordToken)
            })
        };
        request.Headers.Referrer = new Uri(baseUri, "cassia/login");
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Login failed ({(int)response.StatusCode} {response.ReasonPhrase}).");
    }

    private static async Task<string> GetInfoJsonAsync(HttpClient client, Uri baseUri, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "cassia/info");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.Referrer = new Uri(baseUri, "/");

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"/cassia/info failed ({(int)response.StatusCode} {response.ReasonPhrase}).");

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("/cassia/info returned empty response.");

        return json;
    }

    private static async Task PostInfoAsync(HttpClient client, JsonObject payload, Uri baseUri, CancellationToken ct)
    {
        var json = payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, "cassia/info")
        {
            Content = new StringContent(json)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.Referrer = new Uri(baseUri, "/");

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Saving /cassia/info failed ({(int)response.StatusCode} {response.ReasonPhrase}).");
    }

    private static JsonObject ParseInfoObject(string json)
    {
        var node = JsonNode.Parse(json);
        if (node is JsonObject obj)
            return obj;

        throw new InvalidOperationException("/cassia/info did not return a JSON object.");
    }

    private async Task<string?> ResolveCsrfAsync(AuthenticatedSession session, JsonObject infoObject, CancellationToken ct)
    {
        var csrf = TryGetString(infoObject, "csrf");
        if (!string.IsNullOrWhiteSpace(csrf))
            return csrf;

        csrf = TryGetCsrfFromCookies(session.Cookies, session.BaseUri);
        if (!string.IsNullOrWhiteSpace(csrf))
            return csrf;

        csrf = await TryExtractCsrfFromPageAsync(session.Client, session.BaseUri, "cassia/login", ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(csrf))
            return csrf;

        csrf = await TryExtractCsrfFromPageAsync(session.Client, session.BaseUri, "", ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(csrf))
            return csrf;

        return null;
    }

    private static async Task<string?> TryExtractCsrfFromPageAsync(HttpClient client, Uri baseUri, string relativePath, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
            request.Headers.Referrer = new Uri(baseUri, "/");
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var jsonMatch = CsrfJsonRegex.Match(text);
            if (jsonMatch.Success)
                return jsonMatch.Groups["value"].Value;

            var inputMatch = CsrfInputRegex.Match(text);
            if (inputMatch.Success)
                return inputMatch.Groups["value"].Value;
        }
        catch
        {
            // best effort
        }

        return null;
    }

    private static string? TryGetCsrfFromCookies(CookieContainer cookies, Uri baseUri)
    {
        try
        {
            var all = cookies.GetCookies(baseUri);
            foreach (Cookie cookie in all)
            {
                if (cookie == null) continue;
                if (cookie.Name.Contains("csrf", StringComparison.OrdinalIgnoreCase))
                    return cookie.Value;
            }
        }
        catch
        {
            // best effort
        }

        return null;
    }

    private static JsonObject BuildSavePayloadTemplate(JsonObject infoObject, string? csrf)
    {
        var template = new JsonObject();

        foreach (var path in SaveTemplatePaths)
            CopyPathIfExists(infoObject, template, path);

        if (!string.IsNullOrWhiteSpace(csrf))
            template["csrf"] = csrf;

        return template;
    }

    private static void CopyPathIfExists(JsonObject source, JsonObject target, string path)
    {
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return;

        JsonNode? srcNode = source;
        for (var i = 0; i < parts.Length; i++)
        {
            if (srcNode is not JsonObject srcObj || !srcObj.TryGetPropertyValue(parts[i], out srcNode))
                return;
        }

        if (srcNode == null)
            return;

        JsonObject dstObj = target;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var key = parts[i];
            if (dstObj[key] is JsonObject child)
            {
                dstObj = child;
                continue;
            }

            child = new JsonObject();
            dstObj[key] = child;
            dstObj = child;
        }

        dstObj[parts[^1]] = srcNode.DeepClone();
    }

    private IEnumerable<string> BuildPasswordCandidates(CassiaWebSettingsRequest request)
    {
        var values = new List<string>();
        AddDistinct(values, request.PasswordEncrypted);
        AddDistinct(values, request.Password);
        AddDistinct(values, _defaultPasswordEncrypted);
        AddDistinct(values, _defaultPassword);
        return values;
    }

    private static void AddDistinct(ICollection<string> values, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        var normalized = value.Trim();
        if (!values.Contains(normalized, StringComparer.Ordinal))
            values.Add(normalized);
    }

    private string ResolveGatewayAddress(string? gatewayIp)
    {
        var value = string.IsNullOrWhiteSpace(gatewayIp) ? _defaultGatewayAddress : gatewayIp.Trim();
        if (string.IsNullOrWhiteSpace(value))
            value = "192.168.40.1";
        return value;
    }

    private Uri BuildBaseUri(string gatewayAddress)
    {
        var value = gatewayAddress.Trim();
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            value = $"http://{value}";
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"Invalid gateway address '{gatewayAddress}'.");

        var builder = new UriBuilder(uri);
        if (builder.Port <= 0 && _defaultPort > 0)
            builder.Port = _defaultPort;
        if (string.IsNullOrWhiteSpace(builder.Path))
            builder.Path = "/";

        return builder.Uri;
    }

    private static string? TryGetString(JsonObject obj, string propertyName)
    {
        if (!obj.TryGetPropertyValue(propertyName, out var node) || node == null)
            return null;

        return node switch
        {
            JsonValue value when value.TryGetValue<string>(out var str) => str,
            JsonValue value when value.TryGetValue<bool>(out var b) => b ? "true" : "false",
            JsonValue value when value.TryGetValue<int>(out var i) => i.ToString(),
            JsonValue value when value.TryGetValue<long>(out var l) => l.ToString(),
            JsonValue value when value.TryGetValue<double>(out var d) => d.ToString(),
            _ => node.ToJsonString(new JsonSerializerOptions { WriteIndented = false })
        };
    }

    private sealed class AuthenticatedSession : IAsyncDisposable
    {
        public HttpClient Client { get; }
        public Uri BaseUri { get; }
        public CookieContainer Cookies { get; }
        public string InitialInfoJson { get; set; } = "{}";

        public AuthenticatedSession(HttpClient client, Uri baseUri, CookieContainer cookies)
        {
            Client = client;
            BaseUri = baseUri;
            Cookies = cookies;
        }

        public ValueTask DisposeAsync()
        {
            Client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed record CassiaWebSettingsRequest(
    string? GatewayIp = null,
    string? Username = null,
    string? Password = null,
    string? PasswordEncrypted = null);

public sealed record CassiaWebSettingsSnapshot(
    string GatewayIp,
    string? Csrf,
    JsonObject Settings,
    JsonObject SavePayloadTemplate);
