using BeeLogistics.Modules.Map.Application.Services;
using BeeLogistics.Shared.Presentation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Security.Claims;

namespace BeeLogistics.Modules.Map.Presentation.Controllers;

/// <summary>
/// Controller for MQTT credentials management
/// Provides per-driver MQTT tokens for secure direct MQTT connections
/// </summary>
[Route("api/mqtt")]
[Authorize(Roles = "Driver,Owner")]
public class MqttCredentialsController : BaseController
{
    private readonly IMqttTokenService _tokenService;
    private readonly IConfiguration _configuration;

    public MqttCredentialsController(IMqttTokenService tokenService, IConfiguration configuration)
    {
        _tokenService = tokenService;
        _configuration = configuration;
    }

    /// <summary>
    /// Get MQTT credentials for the authenticated driver
    /// Returns connection details and a temporary token for MQTT authentication
    /// </summary>
    [HttpGet("credentials")]
    public async Task<IActionResult> GetCredentials(CancellationToken ct)
    {
        // Get driver ID from JWT token
        var driverIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
            ?? User.FindFirst("sub")?.Value;
        
        if (string.IsNullOrEmpty(driverIdClaim) || !Guid.TryParse(driverIdClaim, out var driverId))
        {
            return Unauthorized(new { success = false, message = "Driver ID not found in token" });
        }

        // Verify user is a driver
        var role = User.FindFirst(ClaimTypes.Role)?.Value ?? User.FindFirst("role")?.Value;
        var isDriver = role == "Driver" || role == "Owner";
        
        if (!isDriver)
        {
            return Forbid("Only drivers can access MQTT credentials");
        }

        try
        {
            // Generate MQTT token
            var tokenResult = await _tokenService.GenerateTokenAsync(driverId, ct);

            if (!tokenResult.IsSuccess)
            {
                return BadRequest(new { success = false, message = tokenResult.ErrorMessage });
            }

            // Get MQTT broker configuration
            var mqttHost = _configuration["Mqtt:Host"] ?? throw new InvalidOperationException("Mqtt:Host is required");
            var mqttPort = int.Parse(_configuration["Mqtt:Port"] ?? "80");
            var useSsl = _configuration["Mqtt:UseSsl"]?.ToLower() != "false" && _configuration["Mqtt:UseSsl"]?.ToLower() != "0";
            var configuredEnv = _configuration["Mqtt:Environment"] ?? _configuration["ASPNETCORE_ENVIRONMENT"];
            var mqttEnvironment = MqttTopicConvention.NormalizeEnvironment(configuredEnv);
            
            // Determine protocol
            var protocol = mqttPort == 443 ? "wss" : (useSsl ? "wss" : "ws");
            var wsUrl = $"{protocol}://{mqttHost}:{mqttPort}/mqtt";

            var topic = MqttTopicConvention.BuildDriverLocationTopic(driverId, mqttEnvironment);

            return Ok(new
            {
                success = true,
                data = new
                {
                    host = mqttHost,
                    port = mqttPort,
                    protocol = protocol,
                    wsUrl = wsUrl,
                    username = $"driver_{driverId}",
                    password = tokenResult.Token, // JWT token used as MQTT password
                    topic = topic,
                    environment = mqttEnvironment,
                    clientId = $"driver-{mqttEnvironment}-{driverId}-{Guid.NewGuid():N}",
                    expiresAt = tokenResult.ExpiresAt,
                    expiresInSeconds = (int)(tokenResult.ExpiresAt - DateTime.UtcNow).TotalSeconds
                }
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = $"Error generating MQTT credentials: {ex.Message}" });
        }
    }

    /// <summary>
    /// Refresh MQTT token (get new token before expiration)
    /// </summary>
    [HttpPost("credentials/refresh")]
    public async Task<IActionResult> RefreshCredentials(CancellationToken ct)
    {
        // Same logic as GetCredentials - just different endpoint for clarity
        return await GetCredentials(ct);
    }
}

