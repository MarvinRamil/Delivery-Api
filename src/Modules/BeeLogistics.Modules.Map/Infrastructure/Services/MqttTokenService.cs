using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Modules.Map.Application.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace BeeLogistics.Modules.Map.Infrastructure.Services;

/// <summary>
/// Service for generating and validating MQTT tokens
/// Uses JWT with driver-specific claims for secure MQTT authentication
/// </summary>
public sealed class MqttTokenService : IMqttTokenService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MqttTokenService> _logger;
    private readonly int _tokenExpiryHours;
    private readonly string _mqttEnvironment;

    public MqttTokenService(
        IServiceScopeFactory serviceScopeFactory,
        IConfiguration configuration,
        ILogger<MqttTokenService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _configuration = configuration;
        _logger = logger;
        _tokenExpiryHours = int.Parse(_configuration["Mqtt:TokenExpiryHours"] ?? "24");
        var configuredEnv = _configuration["Mqtt:Environment"] ?? _configuration["ASPNETCORE_ENVIRONMENT"];
        _mqttEnvironment = MqttTopicConvention.NormalizeEnvironment(configuredEnv);
    }

    public async Task<MqttTokenResult> GenerateTokenAsync(Guid driverId, CancellationToken ct = default)
    {
        try
        {
            // Validate driver exists and is active
            using var scope = _serviceScopeFactory.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            var user = await userManager.FindByIdAsync(driverId.ToString());
            
            if (user == null)
            {
                return new MqttTokenResult
                {
                    IsSuccess = false,
                    ErrorMessage = "Driver not found"
                };
            }

            // Check if driver is active
            if (!user.IsActive)
            {
                return new MqttTokenResult
                {
                    IsSuccess = false,
                    ErrorMessage = "Driver is not active"
                };
            }

            // Check if user is a driver
            var isDriver = user.Role == "Driver";
            if (!isDriver)
            {
                return new MqttTokenResult
                {
                    IsSuccess = false,
                    ErrorMessage = "User is not a driver"
                };
            }

            // Generate JWT token for MQTT authentication
            var jwtSettings = _configuration.GetSection("JwtSettings");
            var secret = jwtSettings["Secret"] ?? throw new InvalidOperationException("JwtSettings:Secret is required");
            var issuer = jwtSettings["Issuer"] ?? "BeeLogisticsApi";
            var audience = "BeeLogisticsMqtt"; // Separate audience for MQTT tokens

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var expiresAt = DateTime.UtcNow.AddHours(_tokenExpiryHours);
            var allowedTopic = MqttTopicConvention.BuildDriverLocationTopic(driverId, _mqttEnvironment);

            // EMQX JWT authenticator ACL claim: restricts this connection to publishing only
            // its own location topic, denying everything else. Enforced by the broker itself,
            // independent of the backend's own topic-format checks, so a driver's token can't
            // be used to spoof another driver's location.
            var aclClaim = JsonSerializer.Serialize(new object[]
            {
                new { permission = "allow", action = "publish", topic = allowedTopic },
                new { permission = "deny", action = "all", topic = "#" }
            });

            // MQTT token claims - minimal, driver-specific
            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, driverId.ToString()), // Driver ID
                new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()), // Token ID
                new("mqtt", "true"), // Flag to identify MQTT tokens
                new("env", _mqttEnvironment),
                new("topic", allowedTopic), // Allowed topic
                new("acl", aclClaim, JsonClaimValueTypes.JsonArray) // Broker-enforced ACL (EMQX)
            };

            var token = new JwtSecurityToken(
                issuer: issuer,
                audience: audience,
                claims: claims,
                expires: expiresAt,
                signingCredentials: creds
            );

            var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

            _logger.LogInformation("Generated MQTT token for driver {DriverId}, expires at {ExpiresAt}", driverId, expiresAt);

            return new MqttTokenResult
            {
                IsSuccess = true,
                Token = tokenString,
                ExpiresAt = expiresAt
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating MQTT token for driver {DriverId}", driverId);
            return new MqttTokenResult
            {
                IsSuccess = false,
                ErrorMessage = $"Error generating token: {ex.Message}"
            };
        }
    }

    public async Task<MqttTokenValidationResult> ValidateTokenAsync(string token, CancellationToken ct = default)
    {
        try
        {
            var jwtSettings = _configuration.GetSection("JwtSettings");
            var secret = jwtSettings["Secret"] ?? throw new InvalidOperationException("JwtSettings:Secret is required");
            var issuer = jwtSettings["Issuer"] ?? "BeeLogisticsApi";
            var audience = "BeeLogisticsMqtt";

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
            var tokenHandler = new JwtSecurityTokenHandler();

            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = issuer,
                ValidAudience = audience,
                IssuerSigningKey = key,
                ClockSkew = TimeSpan.FromMinutes(5) // Allow 5 minute clock skew
            };

            var principal = tokenHandler.ValidateToken(token, validationParameters, out var validatedToken);

            // Extract driver ID from token
            var driverIdClaim = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (string.IsNullOrEmpty(driverIdClaim) || !Guid.TryParse(driverIdClaim, out var driverId))
            {
                return new MqttTokenValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "Invalid driver ID in token"
                };
            }

            // Verify it's an MQTT token
            var mqttClaim = principal.FindFirst("mqtt")?.Value;
            if (mqttClaim != "true")
            {
                return new MqttTokenValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "Token is not an MQTT token"
                };
            }

            // Optional: Verify driver still exists and is active
            using var scope = _serviceScopeFactory.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByIdAsync(driverId.ToString());

            if (user == null || !user.IsActive)
            {
                return new MqttTokenValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "Driver not found or inactive"
                };
            }

            return new MqttTokenValidationResult
            {
                IsValid = true,
                DriverId = driverId
            };
        }
        catch (SecurityTokenExpiredException)
        {
            return new MqttTokenValidationResult
            {
                IsValid = false,
                ErrorMessage = "Token has expired"
            };
        }
        catch (SecurityTokenInvalidSignatureException)
        {
            return new MqttTokenValidationResult
            {
                IsValid = false,
                ErrorMessage = "Invalid token signature"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating MQTT token");
            return new MqttTokenValidationResult
            {
                IsValid = false,
                ErrorMessage = $"Token validation error: {ex.Message}"
            };
        }
    }
}

