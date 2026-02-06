using AccessAPP.Models;
using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;

namespace AccessAPP.Services
{
    public partial class CassiaFirmwareUpgradeService
    {
        private readonly ConcurrentDictionary<string, int> _ledRangeHeldConnections = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _ledRangeGate = new(1, 1);
        private CancellationTokenSource? _ledRangeRunCts;

        private const string LedBreathBlueCmd = "0106020900A5C70102";
        private const string LedBreathGreenCmd = "0106020900A5C70202";
        private const string LedBreathRedCmd = "0106020900A5C70402";

        public sealed class LedRangeRunSummary
        {
            public string RequestId { get; init; } = "";
            public int MinRssi { get; init; }
            public int Requested { get; init; }
            public int Connected { get; init; }
            public int Failed { get; init; }
            public List<string> ConnectedMacs { get; init; } = new();
            public List<string> FailedMacs { get; init; } = new();
        }

        public sealed class LedRangeDisconnectSummary
        {
            public string RequestId { get; init; } = "";
            public int Requested { get; init; }
            public int Disconnected { get; init; }
            public int Failed { get; init; }
        }

        public async Task<LedRangeRunSummary> RunLedRangeVisualizationAsync(
            IReadOnlyList<DeviceListItem> snapshot,
            LedRangeVisualizeCommand command,
            Func<object, Task>? report = null,
            CancellationToken ct = default)
        {
            await _ledRangeGate.WaitAsync(ct).ConfigureAwait(false);
            CancellationTokenSource? runCts = null;
            try
            {
                var requestId = string.IsNullOrWhiteSpace(command.RequestId) ? Guid.NewGuid().ToString("N") : command.RequestId!.Trim();
                var minRssi = command.MinRssi ?? RuntimeVariables.LED_RANGE_MIN_RSSI;
                var pincode = command.Pincode ?? "";
                var useBothChips = command.UseBothChips && RuntimeVariables.USE_BOTH_CASSIA_CHIPS;
                var maxAttempts = Math.Max(3, Math.Max(command.MaxConnectAttempts, RuntimeVariables.LED_RANGE_MAX_CONNECT_ATTEMPTS));
                var parallelPerChip = Math.Max(1, RuntimeVariables.LED_RANGE_PARALLEL_PER_CHIP);
                var autoRetryRounds = Math.Max(0, RuntimeVariables.LED_RANGE_AUTORETRY_ROUNDS);
                var autoRetryDelayMs = Math.Max(0, RuntimeVariables.LED_RANGE_AUTORETRY_DELAY_MS);
                var modelSet = NormalizeModelFilterTokens(command.Models);
                var requestedSensorSet = (command.Sensors ?? new List<string>())
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .Select(m => m.Trim().ToUpperInvariant())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var previous = _ledRangeRunCts;
                previous?.Cancel();
                previous?.Dispose();
                runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _ledRangeRunCts = runCts;
                var runCt = runCts.Token;

                async Task SafeReportAsync(object payload)
                {
                    if (report == null) return;
                    try { await report(payload).ConfigureAwait(false); } catch { }
                }

                var candidates = (snapshot ?? Array.Empty<DeviceListItem>())
                    .Where(d => d != null)
                    .Where(d => !d.IsStale)
                    .Where(d => !string.IsNullOrWhiteSpace(d.MacAddress))
                    .Where(d => requestedSensorSet.Count == 0 || requestedSensorSet.Contains(d.MacAddress.Trim().ToUpperInvariant()))
                    .Where(d => d.Rssi > minRssi)
                    .Where(d => MatchesModel(d, modelSet))
                    .GroupBy(d => d.MacAddress.Trim().ToUpperInvariant(), StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(x => x.Rssi).First())
                    .OrderByDescending(d => d.Rssi)
                    .ToList();

                var candidateByMac = candidates
                    .GroupBy(d => d.MacAddress.Trim().ToUpperInvariant(), StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToDictionary(d => d.MacAddress.Trim().ToUpperInvariant(), StringComparer.OrdinalIgnoreCase);

                await SafeReportAsync(new
                {
                    stage = "started",
                    requestId,
                    minRssi,
                    modelFilter = modelSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
                    requested = candidates.Count,
                    tried = 0,
                    connected = 0,
                    failed = 0,
                    useBothChips,
                    parallelPerChip,
                    autoRetryRounds,
                    autoRetryDelayMs,
                    maxConnectAttempts = maxAttempts,
                    time = DateTimeOffset.UtcNow
                }).ConfigureAwait(false);

                (List<DeviceListItem> chip0, List<DeviceListItem> chip1) BuildChipBuckets(IReadOnlyList<DeviceListItem> devices)
                {
                    var c0 = new List<DeviceListItem>();
                    var c1 = new List<DeviceListItem>();

                    if (useBothChips)
                    {
                        for (var i = 0; i < devices.Count; i++)
                        {
                            if ((i % 2) == 0) c0.Add(devices[i]);
                            else c1.Add(devices[i]);
                        }
                    }
                    else
                    {
                        c0.AddRange(devices);
                    }

                    return (c0, c1);
                }

                var outcomeLock = new object();
                var outcomeByMac = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                var triedCount = 0;
                var connectedCount = 0;
                var failedCount = 0;

                (int Connected, int Failed) RecordOutcome(string mac, bool success)
                {
                    lock (outcomeLock)
                    {
                        if (outcomeByMac.TryGetValue(mac, out var prev))
                        {
                            if (prev != success)
                            {
                                if (success)
                                {
                                    connectedCount++;
                                    failedCount = Math.Max(0, failedCount - 1);
                                }
                                else
                                {
                                    failedCount++;
                                    connectedCount = Math.Max(0, connectedCount - 1);
                                }
                            }
                        }
                        else
                        {
                            if (success) connectedCount++;
                            else failedCount++;
                        }

                        outcomeByMac[mac] = success;
                        return (connectedCount, failedCount);
                    }
                }

                async Task ProcessDeviceAsync(int chip, DeviceListItem dev)
                {
                    runCt.ThrowIfCancellationRequested();
                    var mac = (dev.MacAddress ?? "").Trim().ToUpperInvariant();
                    if (string.IsNullOrWhiteSpace(mac))
                        return;

                    _chipManager.BindMacToChip(mac, chip);
                    var (colorName, colorCmd) = GetLedColorForRssi(dev.Rssi, minRssi);

                    var (success, error) = await TryConnectLoginAndSetLedAsync(
                        mac,
                        pincode,
                        chip,
                        colorCmd,
                        maxAttempts,
                        runCt).ConfigureAwait(false);

                    var triedNow = Interlocked.Increment(ref triedCount);
                    if (success)
                    {
                        _ledRangeHeldConnections[mac] = chip;
                        var (connectedNow, failedNow) = RecordOutcome(mac, true);

                        await SafeReportAsync(new
                        {
                            stage = "connected",
                            requestId,
                            mac,
                            model = ResolveModel(dev),
                            rssi = dev.Rssi,
                            chip,
                            color = colorName,
                            tried = triedNow,
                            requested = candidates.Count,
                            connected = connectedNow,
                            failed = failedNow,
                            time = DateTimeOffset.UtcNow
                        }).ConfigureAwait(false);
                    }
                    else
                    {
                        _chipManager.UnbindMac(mac);
                        var (connectedNow, failedNow) = RecordOutcome(mac, false);

                        await SafeReportAsync(new
                        {
                            stage = "failed",
                            requestId,
                            mac,
                            model = ResolveModel(dev),
                            rssi = dev.Rssi,
                            chip,
                            error,
                            tried = triedNow,
                            requested = candidates.Count,
                            connected = connectedNow,
                            failed = failedNow,
                            time = DateTimeOffset.UtcNow
                        }).ConfigureAwait(false);
                    }
                }

                async Task ProcessOnChipAsync(int chip, List<DeviceListItem> devices)
                {
                    if (devices.Count == 0)
                        return;

                    var queue = new ConcurrentQueue<DeviceListItem>(devices);
                    var workers = Enumerable.Range(0, Math.Min(parallelPerChip, devices.Count))
                        .Select(async _ =>
                        {
                            while (!runCt.IsCancellationRequested && queue.TryDequeue(out var dev))
                            {
                                await ProcessDeviceAsync(chip, dev).ConfigureAwait(false);
                            }
                        })
                        .ToList();

                    await Task.WhenAll(workers).ConfigureAwait(false);
                }

                async Task RunRoundAsync(IReadOnlyList<DeviceListItem> roundDevices)
                {
                    var (chip0, chip1) = BuildChipBuckets(roundDevices);
                    var workers = new List<Task> { ProcessOnChipAsync(0, chip0) };
                    if (useBothChips) workers.Add(ProcessOnChipAsync(1, chip1));
                    await Task.WhenAll(workers).ConfigureAwait(false);
                }

                try
                {
                    await RunRoundAsync(candidates).ConfigureAwait(false);

                    for (var round = 1; round <= autoRetryRounds; round++)
                    {
                        runCt.ThrowIfCancellationRequested();

                        List<string> failedMacs;
                        lock (outcomeLock)
                        {
                            failedMacs = outcomeByMac
                                .Where(kv => !kv.Value)
                                .Select(kv => kv.Key)
                                .ToList();
                        }

                        if (failedMacs.Count == 0)
                            break;

                        var retryDevices = failedMacs
                            .Select(m => candidateByMac.TryGetValue(m, out var dev) ? dev : null)
                            .Where(d => d != null)
                            .Select(d => d!)
                            .ToList();

                        if (retryDevices.Count == 0)
                            break;

                        await SafeReportAsync(new
                        {
                            stage = "retry-round-started",
                            requestId,
                            round,
                            remaining = retryDevices.Count,
                            time = DateTimeOffset.UtcNow
                        }).ConfigureAwait(false);

                        if (autoRetryDelayMs > 0)
                            await Task.Delay(autoRetryDelayMs, runCt).ConfigureAwait(false);

                        await RunRoundAsync(retryDevices).ConfigureAwait(false);

                        await SafeReportAsync(new
                        {
                            stage = "retry-round-completed",
                            requestId,
                            round,
                            tried = Volatile.Read(ref triedCount),
                            connected = Volatile.Read(ref connectedCount),
                            failed = Volatile.Read(ref failedCount),
                            time = DateTimeOffset.UtcNow
                        }).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    await SafeReportAsync(new
                    {
                        stage = "canceled",
                        requestId,
                        requested = candidates.Count,
                        tried = Volatile.Read(ref triedCount),
                        connected = Volatile.Read(ref connectedCount),
                        failed = Volatile.Read(ref failedCount),
                        time = DateTimeOffset.UtcNow
                    }).ConfigureAwait(false);
                }

                List<string> finalConnected;
                List<string> finalFailed;
                lock (outcomeLock)
                {
                    finalConnected = outcomeByMac.Where(kv => kv.Value)
                        .Select(kv => kv.Key)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    finalFailed = outcomeByMac.Where(kv => !kv.Value)
                        .Select(kv => kv.Key)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                var summary = new LedRangeRunSummary
                {
                    RequestId = requestId,
                    MinRssi = minRssi,
                    Requested = candidates.Count,
                    Connected = finalConnected.Count,
                    Failed = finalFailed.Count,
                    ConnectedMacs = finalConnected,
                    FailedMacs = finalFailed
                };

                await SafeReportAsync(new
                {
                    stage = "completed",
                    requestId,
                    minRssi,
                    requested = summary.Requested,
                    tried = Volatile.Read(ref triedCount),
                    connected = summary.Connected,
                    failed = summary.Failed,
                    connectedMacs = summary.ConnectedMacs,
                    failedMacs = summary.FailedMacs,
                    time = DateTimeOffset.UtcNow
                }).ConfigureAwait(false);

                return summary;
            }
            finally
            {
                if (ReferenceEquals(_ledRangeRunCts, runCts))
                {
                    _ledRangeRunCts?.Dispose();
                    _ledRangeRunCts = null;
                }

                _ledRangeGate.Release();
            }
        }

        public async Task<LedRangeDisconnectSummary> DisconnectLedRangeAsync(
            LedRangeDisconnectCommand command,
            Func<object, Task>? report = null,
            CancellationToken ct = default)
        {
            _ledRangeRunCts?.Cancel();

            await _ledRangeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var requestId = string.IsNullOrWhiteSpace(command.RequestId) ? Guid.NewGuid().ToString("N") : command.RequestId!.Trim();
                var forceAll = command.ForceAll;
                var requested = (command.Sensors ?? new List<string>())
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .Select(m => m.Trim().ToUpperInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (requested.Count == 0)
                    forceAll = true;

                if (!forceAll && requested.Count == 0)
                {
                    requested = _ledRangeHeldConnections.Keys
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                if (forceAll || requested.Count == 0)
                {
                    try
                    {
                        var connected = await _connectService.GetConnectedBleDevices(_gatewayIpAddress, _gatewayPort).ConfigureAwait(false);
                        requested = (connected?.nodes ?? new List<Node>())
                            .Select(n => n?.bdaddrs?.Bdaddr ?? "")
                            .Where(m => !string.IsNullOrWhiteSpace(m))
                            .Select(m => m.Trim().ToUpperInvariant())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                            .ToList();
                    }
                    catch { }
                }

                async Task SafeReportAsync(object payload)
                {
                    if (report == null) return;
                    try { await report(payload).ConfigureAwait(false); } catch { }
                }

                var disconnected = 0;
                var failed = 0;

                foreach (var mac in requested)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var preferredChip = _ledRangeHeldConnections.TryGetValue(mac, out var heldChip)
                            ? heldChip
                            : GetChipForMac(mac);

                        var chips = new[] { preferredChip, preferredChip == 0 ? 1 : 0 }.Distinct().ToArray();
                        var ok = false;
                        var lastStatus = HttpStatusCode.InternalServerError;

                        for (var pass = 1; pass <= 2; pass++)
                        {
                            foreach (var chip in chips)
                            {
                                var resp = await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, mac, 0, chip: chip).ConfigureAwait(false);
                                if (resp != null)
                                    lastStatus = resp.Status;

                                if (resp != null && IsDisconnectAccepted(resp.Status))
                                    ok = true;
                            }

                            if (pass == 1)
                                await Task.Delay(2000, ct).ConfigureAwait(false);
                        }

                        if (ok)
                        {
                            disconnected++;
                            _ledRangeHeldConnections.TryRemove(mac, out _);
                            _chipManager.UnbindMac(mac);
                            await SafeReportAsync(new
                            {
                                stage = "disconnected",
                                requestId,
                                mac,
                                chip = preferredChip,
                                time = DateTimeOffset.UtcNow
                            }).ConfigureAwait(false);
                        }
                        else
                        {
                            failed++;
                            await SafeReportAsync(new
                            {
                                stage = "disconnect-failed",
                                requestId,
                                mac,
                                chip = preferredChip,
                                error = lastStatus.ToString(),
                                time = DateTimeOffset.UtcNow
                            }).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        await SafeReportAsync(new
                        {
                            stage = "disconnect-failed",
                            requestId,
                            mac,
                            chip = _ledRangeHeldConnections.TryGetValue(mac, out var c) ? c : GetChipForMac(mac),
                            error = ex.Message,
                            time = DateTimeOffset.UtcNow
                        }).ConfigureAwait(false);
                    }
                }

                var summary = new LedRangeDisconnectSummary
                {
                    RequestId = requestId,
                    Requested = requested.Count,
                    Disconnected = disconnected,
                    Failed = failed
                };

                await SafeReportAsync(new
                {
                    stage = "disconnect-completed",
                    requestId,
                    forceAll,
                    requested = summary.Requested,
                    disconnected = summary.Disconnected,
                    failed = summary.Failed,
                    time = DateTimeOffset.UtcNow
                }).ConfigureAwait(false);

                return summary;
            }
            finally
            {
                _ledRangeGate.Release();
            }
        }

        private async Task<(bool Success, string Error)> TryConnectLoginAndSetLedAsync(
            string mac,
            string pincode,
            int chip,
            string ledCmd,
            int maxAttempts,
            CancellationToken ct)
        {
            string lastError = "Unknown error";
            var retryDelayMs = Math.Max(0, RuntimeVariables.LED_RANGE_CONNECT_RETRY_DELAY_MS);
            var extra417DelayMs = Math.Max(0, RuntimeVariables.LED_RANGE_EXPECTATIONFAILED_EXTRA_DELAY_MS);
            var extra500DelayMs = Math.Max(0, RuntimeVariables.LED_RANGE_INTERNAL_ERROR_EXTRA_DELAY_MS);

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    var connect = await _connectService.ConnectToBleDevice(_gatewayIpAddress, _gatewayPort, mac, chip: chip, useGlobalLock: false).ConfigureAwait(false);
                    if (connect.Status == HttpStatusCode.Conflict)
                    {
                        // Already connected on this gateway; proceed as "connected".
                    }
                    else if (connect.Status != HttpStatusCode.OK)
                    {
                        lastError = $"connect failed ({connect.Status})";
                        if (connect.Status == HttpStatusCode.InternalServerError || connect.Status == HttpStatusCode.ServiceUnavailable)
                            await ForceDisconnectBothChipsAsync(mac).ConfigureAwait(false);
                        else
                            await SafeDisconnectAsync(mac, chip).ConfigureAwait(false);
                        var delayMs = retryDelayMs;
                        if ((int)connect.Status == 417 && extra417DelayMs > 0)
                            delayMs += extra417DelayMs;
                        if ((int)connect.Status == 500 && extra500DelayMs > 0)
                            delayMs += extra500DelayMs;
                        if (delayMs > 0)
                            await Task.Delay(delayMs, ct).ConfigureAwait(false);
                        continue;
                    }

                    var isBootMode = CheckIfDeviceInBootMode(_gatewayIpAddress, mac);
                    if (!isBootMode)
                    {
                        var login = await _connectService.AttemptLogin(_gatewayIpAddress, mac, ct).ConfigureAwait(false);
                        var pincodeRequired = login?.ResponseBody?.PincodeRequired == true;
                        var loginOk = login?.ResponseBody?.Status == HttpStatusCode.OK;

                        if (!loginOk && pincodeRequired && !string.IsNullOrWhiteSpace(pincode))
                        {
                            var pin = await _cassiaPinCodeService.CheckPincode(_gatewayIpAddress, mac, pincode).ConfigureAwait(false);
                            var pinOk = pin?.ResponseBody?.PinCodeAccepted == true;
                            if (pinOk)
                            {
                                login = await _connectService.AttemptLogin(_gatewayIpAddress, mac, ct).ConfigureAwait(false);
                                loginOk = login?.ResponseBody?.Status == HttpStatusCode.OK;
                            }
                        }

                        if (!loginOk)
                        {
                            lastError = "login failed";
                            await SafeDisconnectAsync(mac, chip).ConfigureAwait(false);
                            if (retryDelayMs > 0)
                                await Task.Delay(retryDelayMs, ct).ConfigureAwait(false);
                            continue;
                        }
                    }

                    var (ledOk, ledData, ledError) = await SendLedCommandFastAsync(mac, chip, ledCmd, ct).ConfigureAwait(false);
                    if (!ledOk)
                    {
                        lastError = ledError;
                        await SafeDisconnectAsync(mac, chip).ConfigureAwait(false);
                        if (retryDelayMs > 0)
                            await Task.Delay(retryDelayMs, ct).ConfigureAwait(false);
                        continue;
                    }

                    var data = (ledData ?? "").Trim().ToUpperInvariant();
                    var ok = data == "00" || data == "0000" || string.IsNullOrWhiteSpace(data);
                    if (!ok)
                    {
                        lastError = $"led rejected ({data})";
                        await SafeDisconnectAsync(mac, chip).ConfigureAwait(false);
                        if (retryDelayMs > 0)
                            await Task.Delay(retryDelayMs, ct).ConfigureAwait(false);
                        continue;
                    }

                    return (true, "");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    await SafeDisconnectAsync(mac, chip).ConfigureAwait(false);
                    if (retryDelayMs > 0)
                        await Task.Delay(retryDelayMs, ct).ConfigureAwait(false);
                }
            }

            return (false, lastError);
        }

        private async Task SafeDisconnectAsync(string mac, int chip)
        {
            try
            {
                await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, mac, 0, chip: chip).ConfigureAwait(false);
            }
            catch { }
        }

        private async Task ForceDisconnectBothChipsAsync(string mac)
        {
            try { await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, mac, 0, chip: 0).ConfigureAwait(false); } catch { }
            try { await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, mac, 0, chip: 1).ConfigureAwait(false); } catch { }
        }

        private static (string ColorName, string Command) GetLedColorForRssi(int rssi, int minRssi)
        {
            var greenThreshold = RuntimeVariables.LED_RANGE_GREEN_THRESHOLD;
            var blueThreshold = RuntimeVariables.LED_RANGE_BLUE_THRESHOLD;

            if (greenThreshold < blueThreshold)
                (greenThreshold, blueThreshold) = (blueThreshold, greenThreshold);

            if (rssi > greenThreshold) return ("green", LedBreathGreenCmd);
            if (rssi > blueThreshold) return ("blue", LedBreathBlueCmd);
            if (rssi > minRssi) return ("red", LedBreathRedCmd);
            return ("red", LedBreathRedCmd);
        }

        private static bool IsDisconnectAccepted(HttpStatusCode status) =>
            status == HttpStatusCode.OK ||
            status == HttpStatusCode.NoContent ||
            status == HttpStatusCode.NotFound ||
            status == HttpStatusCode.BadRequest;

        private static HashSet<string> NormalizeModelFilterTokens(List<string>? models)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in models ?? new List<string>())
            {
                var token = (raw ?? "").Trim();
                if (string.IsNullOrWhiteSpace(token))
                    continue;

                if (token.Equals("all", StringComparison.OrdinalIgnoreCase))
                    continue;

                var upper = token.ToUpperInvariant();
                result.Add(upper);

                var alias = NormalizeDetectorTypeToken(upper);
                if (!string.IsNullOrWhiteSpace(alias))
                    result.Add(alias);
            }

            return result;
        }

        private static bool MatchesModel(DeviceListItem dev, HashSet<string> modelSet)
        {
            if (modelSet == null || modelSet.Count == 0)
                return true;

            var model = ResolveModel(dev);
            if (string.IsNullOrWhiteSpace(model))
                return false;

            if (modelSet.Contains(model))
                return true;

            var alias = NormalizeDetectorTypeToken(model);
            return !string.IsNullOrWhiteSpace(alias) && modelSet.Contains(alias);
        }

        private static string ResolveModel(DeviceListItem dev)
        {
            var detectorType = (dev.DetectorType ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(detectorType))
            {
                var normalizedType = NormalizeDetectorTypeToken(detectorType);
                if (!string.IsNullOrWhiteSpace(normalizedType))
                    return normalizedType;
            }

            var productNumber = (dev.ProductNumber ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(productNumber))
            {
                var m = Regex.Match(productNumber, @"P\d{2}", RegexOptions.IgnoreCase);
                if (m.Success)
                    return NormalizeDetectorTypeToken(m.Value) ?? m.Value.ToUpperInvariant();
            }

            return "";
        }

        private static string? NormalizeDetectorTypeToken(string token)
        {
            var t = (token ?? "").Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(t))
                return null;

            if (t.Contains("MASTER")) return "MASTER";
            if (t.Contains("SECONDARY")) return "SECONDARY";
            if (t == "BMS" || t.Contains("BMS")) return "BMS";

            return t switch
            {
                "P41" => "SECONDARY",
                "P42" => "MASTER",
                "P46" => "BMS",
                "P47" => "MASTER",
                "P48" => "BMS",
                _ => null
            };
        }

        private async Task<(bool Ok, string Data, string Error)> SendLedCommandFastAsync(string mac, int chip, string ledCmd, CancellationToken ct)
        {
            Guid token = Guid.Empty;
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                token = _notificationService.Subscribe(mac, (_, data) =>
                {
                    try
                    {
                        var parsed = new HelperClasses.GenericTelegramReply(data);
                        var payload = (parsed.DataResult ?? "").Trim();
                        if (!string.IsNullOrWhiteSpace(payload))
                            tcs.TrySetResult(payload);
                    }
                    catch { }
                });

                using var _ = await cassiaReadWriteService
                    .WriteBleMessageAsync(_gatewayIpAddress, mac, 19, ledCmd, "?noresponse=1", chip: chip, ct: ct)
                    .ConfigureAwait(false);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));

                try
                {
                    var completed = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token)).ConfigureAwait(false);
                    if (completed == tcs.Task)
                    {
                        var data = (await tcs.Task.ConfigureAwait(false)).Trim().ToUpperInvariant();
                        return (true, data, "");
                    }
                }
                catch (OperationCanceledException)
                {
                    // No BLE notify within fast timeout. REST write already succeeded.
                }

                return (true, "", "");
            }
            catch (Exception ex)
            {
                return (false, "", $"led write failed ({ex.Message})");
            }
            finally
            {
                if (token != Guid.Empty)
                    _notificationService.Unsubscribe(mac, token);
            }
        }
    }
}
