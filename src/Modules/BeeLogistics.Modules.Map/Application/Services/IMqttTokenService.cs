namespace BeeLogistics.Modules.Map.Application.Services;

/// <summary>
/// Service for generating and validating MQTT tokens for drivers
/// Tokens are JWT-based and include driver ID and expiration
/// </summary>
public interface IMqttTokenService
{
    /// <summary>
    /// Generate a new MQTT token for a driver
    /// </summary>
    /// <param name="driverId">Driver ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Token generation result</returns>
    Task<MqttTokenResult> GenerateTokenAsync(Guid driverId, CancellationToken ct = default);

    /// <summary>
    /// Validate an MQTT token and extract driver ID
    /// </summary>
    /// <param name="token">MQTT token (JWT)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Validation result with driver ID if valid</returns>
    Task<MqttTokenValidationResult> ValidateTokenAsync(string token, CancellationToken ct = default);
}

/// <summary>
/// Result of MQTT token generation
/// </summary>
public sealed class MqttTokenResult
{
    public bool IsSuccess { get; init; }
    public string? Token { get; init; }
    public DateTime ExpiresAt { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Result of MQTT token validation
/// </summary>
public sealed class MqttTokenValidationResult
{
    public bool IsValid { get; init; }
    public Guid? DriverId { get; init; }
    public string? ErrorMessage { get; init; }
}

