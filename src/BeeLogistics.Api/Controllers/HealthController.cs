using BeeLogistics.Modules.CRM.Infrastructure;
using BeeLogistics.Modules.Map.Application.Services;
using BeeLogistics.Modules.Verification.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace BeeLogistics.Api.Controllers;

/// <summary>
/// Health check endpoint for load balancers and monitoring.
/// NOTE: Inherits from ControllerBase (not BaseController) because:
/// - Must be publicly accessible without authentication
/// - Used by Docker/Kubernetes health probes
/// </summary>
[ApiController]
[Route("health")]
public class HealthController : ControllerBase
{
    private readonly IMqttConnectionStatus? _mqttStatus;
    private readonly ILivenessApiClient? _livenessClient;
    private readonly ZammadService? _zammadService;

    public HealthController(
        IMqttConnectionStatus? mqttStatus = null,
        ILivenessApiClient? livenessClient = null,
        ZammadService? zammadService = null)
    {
        _mqttStatus = mqttStatus;
        _livenessClient = livenessClient;
        _zammadService = zammadService;
    }

    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new
        {
            status = "healthy",
            timestamp = DateTime.UtcNow,
            service = "BeeLogistics API"
        });
    }

    /// <summary>
    /// MQTT subscriber connection status for monitoring and debugging.
    /// Use this or app logs to verify MQTT is connected and receiving messages.
    /// </summary>
    [HttpGet("mqtt")]
    public IActionResult GetMqttStatus()
    {
        if (_mqttStatus == null)
        {
            return Ok(new { configured = false, message = "MQTT status not available" });
        }

        return Ok(new
        {
            configured = true,
            isConnected = _mqttStatus.IsConnected,
            timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// YOLO liveness service health: whether it is configured and if we can reach it (GET /v1/health).
    /// </summary>
    [HttpGet("liveness")]
    public async Task<IActionResult> GetLivenessStatus(CancellationToken cancellationToken)
    {
        if (_livenessClient == null)
        {
            return Ok(new { configured = false, reachable = false, message = "Liveness not available" });
        }

        var (configured, reachable, error) = await _livenessClient.CheckHealthAsync(cancellationToken);
        return Ok(new
        {
            configured,
            reachable,
            error,
            timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Zammad helpdesk connection status. Verifies Zammad is reachable and API token is valid.
    /// Filter logs by "zammad" prefix for Zammad-related messages.
    /// </summary>
    [HttpGet("zammad")]
    public async Task<IActionResult> GetZammadStatus(CancellationToken cancellationToken)
    {
        if (_zammadService == null)
        {
            return Ok(new { configured = false, connected = false, message = "Zammad not available" });
        }

        var (connected, error) = await _zammadService.CheckConnectionAsync(cancellationToken);
        return Ok(new
        {
            configured = true,
            connected,
            error,
            timestamp = DateTime.UtcNow
        });
    }
}

