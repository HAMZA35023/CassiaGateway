using MQTTnet;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using System.Buffers;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace AccessAPP.Services;

public sealed class MqttService : IMqttService
{
    private readonly MqttConfigStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private MQTTnet.IMqttClient? _client;
    private CancellationTokenSource? _runCts;
    private Task? _runLoop;

    private bool _subscribed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public MqttOptions CurrentOptions { get; private set; }

    public event Func<StartUpdateCommand, Task>? StartUpdateRequested;
    public event Func<GetFwVersionCommand, Task>? GetFwVersionRequested;

    public MqttService(MqttConfigStore store)
    {
        _store = store;
        CurrentOptions = _store.LoadOrCreateDefault();
    }

    // ---------------- Public API ----------------

    public async Task StartAsync(CancellationToken ct = default)
    {
        Log("Starting MQTT service");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_runLoop is not null)
            {
                Log("Already running");
                return;
            }

            _runCts = new CancellationTokenSource();
            _runLoop = Task.Run(() => RunLoopAsync(_runCts.Token), CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        Log("Stopping MQTT service");

        Task? loop;
        MQTTnet.IMqttClient? client;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_runLoop is null) return;

            _runCts?.Cancel();
            loop = _runLoop;
            _runLoop = null;

            client = _client;
            _client = null;
            _subscribed = false;
        }
        finally
        {
            _gate.Release();
        }

        if (client is not null)
        {
            try
            {
                if (client.IsConnected)
                    await DisconnectAsyncViaReflection(client, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"Disconnect error: {ex.Message}");
            }

            try { client.Dispose(); } catch { /* ignore */ }
        }

        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    public async Task UpdateIdentityAsync(string name, string networkId, bool persist = true, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name cannot be empty.", nameof(name));
        if (string.IsNullOrWhiteSpace(networkId)) throw new ArgumentException("NetworkId cannot be empty.", nameof(networkId));

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CurrentOptions.Name = name.Trim();
            CurrentOptions.NetworkId = networkId.Trim();
            if (persist) _store.Save(CurrentOptions);
        }
        finally
        {
            _gate.Release();
        }

        await RestartAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateBrokerAsync(string host, int port, string? username, string? password, bool useTls, bool persist = true, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host cannot be empty.", nameof(host));
        if (port <= 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CurrentOptions.Host = host.Trim();
            CurrentOptions.Port = port;
            CurrentOptions.Username = string.IsNullOrWhiteSpace(username) ? null : username;
            CurrentOptions.Password = string.IsNullOrWhiteSpace(password) ? null : password;
            CurrentOptions.UseTls = useTls;
            if (persist) _store.Save(CurrentOptions);
        }
        finally
        {
            _gate.Release();
        }

        await RestartAsync(ct).ConfigureAwait(false);
    }

    public async Task PublishDiscoveredDevicesAsync(DiscoveredDevicesMessage msg, CancellationToken ct = default)
    {
        msg.Name = CurrentOptions.Name;
        msg.NetworkId = CurrentOptions.NetworkId;
        if (msg.Time == default) msg.Time = DateTimeOffset.UtcNow;

        await PublishJsonAsync(TeleTopic("discovered"), msg, retain: false, ct).ConfigureAwait(false);
    }

    public async Task PublishUpdateProgressAsync(UpdateProgressMessage msg, CancellationToken ct = default)
    {
        msg.Name = CurrentOptions.Name;
        msg.NetworkId = CurrentOptions.NetworkId;
        if (msg.Time == default) msg.Time = DateTimeOffset.UtcNow;

        await PublishJsonAsync(TeleTopic("progress"), msg, retain: false, ct).ConfigureAwait(false);
    }

    public async Task PublishLogAsync(LogMessage msg, CancellationToken ct = default)
    {
        msg.Name = CurrentOptions.Name;
        msg.NetworkId = CurrentOptions.NetworkId;
        if (msg.Time == default) msg.Time = DateTimeOffset.UtcNow;

        await PublishJsonAsync(TeleTopic("log"), msg, retain: false, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
        _runCts?.Dispose();
    }

    // ---------------- Run loop / connection ----------------

    private async Task RestartAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var running = _runLoop is not null;
        _gate.Release();

        if (!running) return;

        await StopAsync(ct).ConfigureAwait(false);
        await StartAsync(ct).ConfigureAwait(false);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        Log("Run loop started");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                Log($"Ensuring connection to {CurrentOptions.Host}:{CurrentOptions.Port} (clientId={CurrentOptions.ClientId}, network={CurrentOptions.NetworkId})");
                await EnsureConnectedAndSubscribedAsync(ct).ConfigureAwait(false);

                // retained online status
                var online = new StatusMessage
                {
                    Name = CurrentOptions.Name,
                    NetworkId = CurrentOptions.NetworkId,
                    Time = DateTimeOffset.UtcNow,
                    State = "online"
                };
                await PublishJsonAsync(TeleTopic("status"), online, retain: true, ct).ConfigureAwait(false);
                Log("Published retained online status");

                while (!ct.IsCancellationRequested && _client is not null && _client.IsConnected)
                    await Task.Delay(500, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Log("Run loop cancelled");
            }
            catch (Exception ex)
            {
                Log($"Connection error: {ex.Message}");
            }

            if (!ct.IsCancellationRequested)
            {
                var delay = Math.Max(1, CurrentOptions.ReconnectDelaySeconds);
                Log($"Reconnecting in {delay}s...");
                try { await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false); }
                catch { /* ignore */ }
            }
        }

        Log("Run loop exited");
    }

    private async Task EnsureConnectedAndSubscribedAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is null)
            {
                Log("Creating MQTT client via MqttClientFactory");
                _client = new MqttClientFactory().CreateMqttClient();
                _subscribed = false;

                // Use reflection to hook handlers safely across variants
                HookHandlersViaReflection(_client);

                // Some variants expose DisconnectedAsync event with args (we already log in handler hook)
            }

            if (_client.IsConnected) return;

            _subscribed = false;

            Log("Connecting to broker...");
            var opts = BuildOptionsObject();
            await ConnectAsyncViaReflection(_client, opts, ct).ConfigureAwait(false);
            Log("Connected");

            // Subscribe
            await SubscribeTopicsAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SubscribeTopicsAsync(CancellationToken ct)
    {
        if (_client is null) return;
        if (_subscribed) return;

        var topicMine = CmdTopic(CurrentOptions.Name, "#");
        Log($"Subscribing: {topicMine}");
        await _client.SubscribeAsync(new MqttTopicFilter
        {
            Topic = topicMine,
            QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce
        }, ct).ConfigureAwait(false);

        if (CurrentOptions.SubscribeToAllTarget)
        {
            var topicAll = CmdTopic("all", "#");
            Log($"Subscribing: {topicAll}");
            await _client.SubscribeAsync(new MqttTopicFilter
            {
                Topic = topicAll,
                QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce
            }, ct).ConfigureAwait(false);
        }

        _subscribed = true;
        Log("Subscriptions active");
    }

    // ---------------- Publish / Receive ----------------

    private Task OnMessageReceivedCoreAsync(string topic, object payloadObj)
    {
        Log($"RX topic: {topic}");

        var payloadBytes = GetPayloadBytes(payloadObj);
        var payload = payloadBytes.Length == 0 ? "" : Encoding.UTF8.GetString(payloadBytes);

        // accessapp/{networkId}/cmd/{target}/{command}
        var topicParts = topic.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (topicParts.Length < 5) return Task.CompletedTask;

        var baseTopic = topicParts[0];
        var networkId = topicParts[1];
        var cmdLiteral = topicParts[2];
        var command = topicParts[4];

        if (!string.Equals(baseTopic, CurrentOptions.BaseTopic, StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;
        if (!string.Equals(cmdLiteral, "cmd", StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;
        if (!string.Equals(networkId, CurrentOptions.NetworkId, StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;

        try
        {
            if (string.Equals(command, "start-update", StringComparison.OrdinalIgnoreCase))
            {
                Log($"Command: start-update ({payload})");
                var dto = JsonSerializer.Deserialize<StartUpdateCommand>(payload, JsonOptions) ?? new StartUpdateCommand();
                return StartUpdateRequested?.Invoke(dto) ?? Task.CompletedTask;
            }

            if (string.Equals(command, "get-fw-version", StringComparison.OrdinalIgnoreCase))
            {
                Log($"Command: get-fw-version ({payload})");
                var dto = JsonSerializer.Deserialize<GetFwVersionCommand>(payload, JsonOptions) ?? new GetFwVersionCommand();
                return GetFwVersionRequested?.Invoke(dto) ?? Task.CompletedTask;
            }
        }
        catch (Exception ex)
        {
            Log($"Command parse/dispatch error: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private async Task PublishJsonAsync(string topic, object payload, bool retain, CancellationToken ct)
    {
        await EnsureConnectedAndSubscribedAsync(ct).ConfigureAwait(false);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        //Log($"TX topic: {topic} (retain={retain}, {json.Length} bytes)");

        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(Encoding.UTF8.GetBytes(json))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .Build();

        var client = _client;
        if (client is not null && client.IsConnected)
            await client.PublishAsync(msg, ct).ConfigureAwait(false);
    }

    // ---------------- Topic helpers ----------------

    private string TeleTopic(string leaf)
        => $"{CurrentOptions.BaseTopic}/{CurrentOptions.NetworkId}/tele/{CurrentOptions.Name}/{leaf}";

    private string CmdTopic(string target, string leaf)
        => $"{CurrentOptions.BaseTopic}/{CurrentOptions.NetworkId}/cmd/{target}/{leaf}";

    // ---------------- Options construction (reflection based, no MQTTnet.Client namespace) ----------------

    private object BuildOptionsObject()
    {
        var o = CurrentOptions;
        var mqttAsm = typeof(MqttClientFactory).Assembly;

        // Exact MqttClientOptions type from the currently loaded MQTTnet assembly
        var optionsType = mqttAsm.GetType("MQTTnet.MqttClientOptions")
                         ?? mqttAsm.GetTypes().FirstOrDefault(t => t.FullName == "MQTTnet.MqttClientOptions");

        if (optionsType == null)
            throw new InvalidOperationException("Cannot find MQTTnet.MqttClientOptions type in current MQTTnet assembly.");

        var options = Activator.CreateInstance(optionsType)
                      ?? throw new InvalidOperationException("Failed to create MQTT client options instance.");

        // Set simple properties if present
        TrySetProp(options, "ClientId", o.ClientId);
        TrySetProp(options, "KeepAlivePeriod", TimeSpan.FromSeconds(Math.Max(5, o.KeepAliveSeconds)));
        TrySetProp(options, "CleanSession", true);

        // Build TCP options
        var tcpType = mqttAsm.GetType("MQTTnet.MqttClientTcpOptions")
                     ?? mqttAsm.GetTypes().FirstOrDefault(t => t.FullName == "MQTTnet.MqttClientTcpOptions");

        if (tcpType == null)
            throw new InvalidOperationException("Cannot find MQTTnet.MqttClientTcpOptions type in current MQTTnet assembly.");

        var tcp = Activator.CreateInstance(tcpType)
                  ?? throw new InvalidOperationException("Failed to create TCP options instance.");
        Log("TCP options props: " + string.Join(", ", tcp.GetType().GetProperties().Select(p => p.Name)));


        ApplyTcpEndpoint(tcp, o.Host, o.Port);

        // Attach TCP options to main options
        if (!TrySetProp(options, "ChannelOptions", tcp))
            TrySetProp(options, "TransportOptions", tcp);

        // Credentials via provider interface (your build uses IMqttClientCredentialsProvider)
        if (!string.IsNullOrWhiteSpace(o.Username))
        {
            ApplyCredentialsProvider(options, o.Username!, o.Password ?? "");
        }

        // TLS: your build doesn't expose builder TLS helpers; handle later if needed.
        // If your MqttClientTcpOptions exposes a TlsOptions property, we can set it similarly.

        return options;
    }

    private static void ApplyTcpEndpoint(object tcpOptions, string host, int port)
    {
        var t = tcpOptions.GetType();

        // Your build exposes RemoteEndpoint (see props dump)
        var pRemote = t.GetProperty("RemoteEndpoint");
        if (pRemote != null && pRemote.CanWrite)
        {
            // RemoteEndpoint usually expects System.Net.EndPoint (IPEndPoint)
            if (typeof(System.Net.EndPoint).IsAssignableFrom(pRemote.PropertyType))
            {
                // IP is preferred. If host is not IP, try DNS resolve (best-effort).
                System.Net.IPAddress? ip = null;
                if (!System.Net.IPAddress.TryParse(host, out var parsedIp))
                {
                    try
                    {
                        var addrs = System.Net.Dns.GetHostAddresses(host);
                        ip = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                             ?? addrs.FirstOrDefault();
                    }
                    catch
                    {
                        ip = null;
                    }
                }
                else
                {
                    ip = parsedIp;
                }

                if (ip == null)
                    throw new InvalidOperationException($"Cannot resolve MQTT broker host '{host}' to an IP address.");

                var ep = new System.Net.IPEndPoint(ip, port);
                pRemote.SetValue(tcpOptions, ep);
                return;
            }
        }

        // Fallbacks (in case your build changes later)
        TrySetProp(tcpOptions, "RemoteEndPoint", $"{host}:{port}");
        TrySetProp(tcpOptions, "Endpoint", $"{host}:{port}");
    }


    private static void ApplyCredentialsProvider(object options, string username, string password)
    {
        var optType = options.GetType();
        var credProp =
            optType.GetProperty("CredentialsProvider") ??
            optType.GetProperty("CredentialProvider") ??
            optType.GetProperty("Credentials");

        if (credProp == null || !credProp.CanWrite)
        {
            // Some builds use Username/Password directly on options
            TrySetProp(options, "Username", username);
            TrySetProp(options, "Password", password);
            return;
        }

        var iface = credProp.PropertyType;
        var mqttAsm = typeof(MqttClientFactory).Assembly;

        // Find a concrete type that implements the interface and has ctor(string,string) or ctor(string,byte[])
        var candidates = mqttAsm
            .GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && iface.IsAssignableFrom(t))
            .ToList();

        foreach (var t in candidates)
        {
            var c1 = t.GetConstructor(new[] { typeof(string), typeof(string) });
            if (c1 != null)
            {
                var inst = c1.Invoke(new object[] { username, password });
                credProp.SetValue(options, inst);
                return;
            }

            var c2 = t.GetConstructor(new[] { typeof(string), typeof(byte[]) });
            if (c2 != null)
            {
                var inst = c2.Invoke(new object[] { username, Encoding.UTF8.GetBytes(password) });
                credProp.SetValue(options, inst);
                return;
            }
        }

        throw new InvalidOperationException(
            $"MQTTnet credentials provider interface found ({iface.FullName}) but no concrete provider with expected ctor was found.");
    }

    private static bool TrySetProp(object obj, string name, object? value)
    {
        var p = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        if (p == null || !p.CanWrite) return false;

        try
        {
            if (value == null)
            {
                p.SetValue(obj, null);
                return true;
            }

            var targetType = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;

            if (targetType.IsAssignableFrom(value.GetType()))
            {
                p.SetValue(obj, value);
                return true;
            }

            var converted = Convert.ChangeType(value, targetType);
            p.SetValue(obj, converted);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------------- Payload extraction (supports multiple shapes) ----------------

    private static byte[] GetPayloadBytes(object payloadObj)
    {
        if (payloadObj is null) return Array.Empty<byte>();
        if (payloadObj is byte[] b) return b;
        if (payloadObj is ReadOnlyMemory<byte> rom) return rom.ToArray();
        if (payloadObj is ReadOnlySequence<byte> ros) return ros.ToArray();

        // Some builds use property "Payload" which is ReadOnlySequence<byte> or byte[]
        var p = payloadObj.GetType().GetProperty("ToArray", BindingFlags.Instance | BindingFlags.Public);
        if (p != null && p.GetIndexParameters().Length == 0)
        {
            try
            {
                var v = p.GetValue(payloadObj);
                if (v is byte[] bb) return bb;
            }
            catch { }
        }

        return Array.Empty<byte>();
    }

    // ---------------- Reflection invocation helpers ----------------

    private static Task ConnectAsyncViaReflection(MQTTnet.IMqttClient client, object options, CancellationToken ct)
    {
        var clientType = client.GetType();
        var optionsType = options.GetType();

        var mi = clientType.GetMethod("ConnectAsync", new[] { optionsType, typeof(CancellationToken) });
        if (mi == null)
        {
            // Try ConnectAsync(options) overload
            mi = clientType.GetMethod("ConnectAsync", new[] { optionsType });
            if (mi == null)
            {
                var sigs = string.Join(" | ",
                    clientType.GetMethods().Where(m => m.Name == "ConnectAsync").Select(m => m.ToString()));
                throw new InvalidOperationException($"Could not find ConnectAsync overload for options type {optionsType.FullName}. Overloads: {sigs}");
            }

            var r0 = mi.Invoke(client, new[] { options });
            return r0 as Task ?? throw new InvalidOperationException("ConnectAsync returned unexpected type.");
        }

        var r = mi.Invoke(client, new[] { options, ct });
        return r as Task ?? throw new InvalidOperationException("ConnectAsync returned unexpected type.");
    }

    private static Task DisconnectAsyncViaReflection(MQTTnet.IMqttClient client, CancellationToken ct)
    {
        var t = client.GetType();

        // Try DisconnectAsync(CancellationToken)
        var mi = t.GetMethod("DisconnectAsync", new[] { typeof(CancellationToken) });
        if (mi != null)
        {
            var r = mi.Invoke(client, new object[] { ct });
            return r as Task ?? Task.CompletedTask;
        }

        // Try DisconnectAsync()
        mi = t.GetMethod("DisconnectAsync", Type.EmptyTypes);
        if (mi != null)
        {
            var r = mi.Invoke(client, Array.Empty<object>());
            return r as Task ?? Task.CompletedTask;
        }

        return Task.CompletedTask;
    }

    private void HookHandlersViaReflection(MQTTnet.IMqttClient client)
    {
        // Hook ApplicationMessageReceivedAsync
        // Different builds expose either:
        //  - event ApplicationMessageReceivedAsync: Func<MqttApplicationMessageReceivedEventArgs, Task>
        //  - event ApplicationMessageReceived: ...
        //
        // We'll try to hook ApplicationMessageReceivedAsync first.
        TryHookAsyncEvent(
            client,
            "ApplicationMessageReceivedAsync",
            (argsObj) =>
            {
                try
                {
                    var topic = (string?)argsObj.GetType().GetProperty("ApplicationMessage")?.GetValue(argsObj)?
                        .GetType().GetProperty("Topic")?.GetValue(
                            argsObj.GetType().GetProperty("ApplicationMessage")!.GetValue(argsObj)!, null);

                    topic ??= "";

                    var appMsg = argsObj.GetType().GetProperty("ApplicationMessage")!.GetValue(argsObj)!;
                    var payloadObj = appMsg.GetType().GetProperty("Payload")?.GetValue(appMsg) ?? appMsg.GetType().GetProperty("PayloadSegment")?.GetValue(appMsg);

                    return OnMessageReceivedCoreAsync(topic, payloadObj ?? Array.Empty<byte>());
                }
                catch (Exception ex)
                {
                    Log($"RX handler error: {ex.Message}");
                    return Task.CompletedTask;
                }
            });

        // Hook DisconnectedAsync (optional logging)
        TryHookAsyncEvent(
            client,
            "DisconnectedAsync",
            (_argsObj) =>
            {
                Log("Disconnected");
                _subscribed = false;
                return Task.CompletedTask;
            });
    }

    private void TryHookAsyncEvent(MQTTnet.IMqttClient client, string eventName, Func<object, Task> handler)
    {
        try
        {
            var evt = client.GetType().GetEvent(eventName, BindingFlags.Instance | BindingFlags.Public);
            if (evt == null) return;

            var delType = evt.EventHandlerType!;
            // Create delegate matching signature: (TArgs) => Task
            var invoke = delType.GetMethod("Invoke")!;
            var parms = invoke.GetParameters();
            if (parms.Length != 1) return;

            var argsType = parms[0].ParameterType;

            // Build: Task Handler(TArgs args) => handler(args)
            var mi = GetType().GetMethod(nameof(BridgeAsync), BindingFlags.Instance | BindingFlags.NonPublic)!;
            var gmi = mi.MakeGenericMethod(argsType);

            var del = Delegate.CreateDelegate(delType, this, gmi);
            evt.AddEventHandler(client, del);
            Log($"Hooked event: {eventName}");
        }
        catch (Exception ex)
        {
            Log($"Failed to hook {eventName}: {ex.Message}");
        }

        // Store handler
        _bridgeHandler = handler;
    }

    private Func<object, Task>? _bridgeHandler;

    private Task BridgeAsync<TArgs>(TArgs args)
    {
        var h = _bridgeHandler;
        if (h == null) return Task.CompletedTask;
        return h(args!);
    }

    // ---------------- Logging ----------------

    private void Log(string msg)
    {
        Console.WriteLine($"[MQTT {DateTime.Now:HH:mm:ss}] {msg}");
    }
}
