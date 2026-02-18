using System.Globalization;
using AccessAPP.Services;
using Microsoft.AspNetCore.Mvc;

namespace AccessAPP.Controllers;

[ApiController]
[Route("api/panel/v1")]
public sealed class PanelController : ControllerBase
{
    private readonly IMqttService _mqttService;
    private readonly Modem4GStatusService _modem4GStatusService;

    public PanelController(
        IMqttService mqttService,
        Modem4GStatusService modem4GStatusService)
    {
        _mqttService = mqttService;
        _modem4GStatusService = modem4GStatusService;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus([FromQuery] string? panelId, CancellationToken ct)
    {
        var normalizedPanelId = Normalize(panelId);
        if (string.IsNullOrWhiteSpace(normalizedPanelId))
            return BadRequest(Error("Missing panelId."));
        var cassiaId = ResolveLocalCassiaId();

        Modem4GSnapshot? modem = null;
        try
        {
            modem = await _modem4GStatusService.GetLatestAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Panel status LTE read failed: {ex.Message}");
        }

        var programmingList = CassiaFirmwareUpgradeService.GetProgrammingListSnapshot();
        var programmingByChip = BuildProgrammingByChip(programmingList);
        var speedSnapshot = CassiaFirmwareUpgradeService.GetSpeedSnapshot();

        var (lastErrorMessage, lastErrorAtUtc) = AppLog.GetLastError();
        var hasError = lastErrorAtUtc.HasValue;
        var lastErrorAgeSec = hasError
            ? Math.Max(0, (int)(DateTimeOffset.UtcNow - lastErrorAtUtc!.Value).TotalSeconds)
            : 0;

        var cassiaName = Normalize(_mqttService.CurrentOptions.Name) ?? cassiaId;
        var networkId = _mqttService.CurrentOptions.NetworkId ?? "";

        return Ok(new
        {
            panel = new
            {
                id = normalizedPanelId,
                assignedCassiaId = cassiaId
            },
            cassia = new
            {
                id = cassiaId,
                name = cassiaName,
                networkId,
                online = true
            },
            work = new
            {
                queued = CassiaFirmwareUpgradeService.inQueue,
                programming = CassiaFirmwareUpgradeService.GetProgrammingCount(),
                programmingByChip,
                totalSpeedpct = Math.Round(speedSnapshot.TotalPctPerMin, 1),
                totalSpeedAvg10sPct = Math.Round(speedSnapshot.TotalAvg10sPctPerMin, 1),
                workersWithSpeed = speedSnapshot.WorkersWithSpeed
            },
            lte = new
            {
                bars = modem?.SignalBar ?? 0
            },
            health = new
            {
                lastError = hasError ? lastErrorMessage : "-",
                lastErrorAgeSec
            }
        });
    }

    [HttpPost("action")]
    public IActionResult Action([FromBody] PanelActionRequest? body)
    {
        if (body == null)
            return BadRequest(Error("Request body is required."));

        var panelId = Normalize(body.PanelId);
        if (string.IsNullOrWhiteSpace(panelId))
            return BadRequest(Error("Missing panelId."));
        var effectiveCassiaId = ResolveLocalCassiaId();

        var action = Normalize(body.Action)?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(action))
            return BadRequest(Error("Missing action."));

        switch (action)
        {
            case "identify":
                AppLog.Info($"Panel action identify requested (panelId={panelId}, cassiaId={effectiveCassiaId})");
                return Ok(new { ok = true });

            default:
                return StatusCode(
                    StatusCodes.Status409Conflict,
                    Error($"Action '{body.Action}' is not allowed in current state."));
        }
    }

    private string ResolveLocalCassiaId()
    {
        var mqttName = Normalize(_mqttService.CurrentOptions.Name);
        if (!string.IsNullOrWhiteSpace(mqttName))
            return mqttName;

        return "cassia-local";
    }

    private static Dictionary<string, int> BuildProgrammingByChip(
        IReadOnlyList<(string Mac, string DetectorType, string FirmwareVersion)> programmingList)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["0"] = 0,
            ["1"] = 0
        };

        foreach (var item in programmingList)
        {
            var chip = AccessAPP.RuntimeVariables.DEFAULT_CASSIA_CHIP;
            if (CassiaChipManager.TryGetChip(item.Mac, out var resolvedChip))
                chip = resolvedChip;

            if (chip is not (0 or 1))
                continue;

            var key = chip.ToString(CultureInfo.InvariantCulture);
            result[key] = result.TryGetValue(key, out var current) ? current + 1 : 1;
        }

        return result;
    }

    private static object Error(string message) => new { ok = false, error = message };

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim();
    }
}

public sealed class PanelActionRequest
{
    public string? PanelId { get; set; }
    public string? Action { get; set; }
    public string? CassiaId { get; set; }
}
