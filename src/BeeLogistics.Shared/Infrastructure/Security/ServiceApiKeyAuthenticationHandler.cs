using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Shared.Infrastructure.Security;

public static class ServiceApiKeyDefaults
{
    public const string SchemeName = "ServiceApiKey";
    public const string ClientIdHeader = "X-Client-Id";
    public const string ApiKeyHeader = "X-Api-Key";
    public const string ActingAdminHeader = "X-Acting-Admin";
}

/// <summary>
/// A trusted service client (e.g. the back-office backend) allowed to call
/// admin endpoints machine-to-machine. Only SHA-256 hashes of API keys are
/// stored in configuration (Vault-overlaid); multiple hashes per client allow
/// zero-downtime key rotation.
/// </summary>
public class ServiceClientCredential
{
    public string ClientId { get; set; } = string.Empty;
    /// <summary>Lowercase hex SHA-256 hashes of accepted API keys.</summary>
    public List<string> ApiKeyHashes { get; set; } = new();
    /// <summary>Identity user id the service acts as (seeded, login-disabled).</summary>
    public string ServiceUserId { get; set; } = string.Empty;
}

public class ServiceClientsOptions
{
    public const string SectionName = "ServiceClients";
    public List<ServiceClientCredential> Clients { get; set; } = new();
}

/// <summary>
/// Authenticates service-to-service requests carrying X-Client-Id + X-Api-Key headers.
/// On success the principal carries SuperAdmin + is_backoffice claims so the existing
/// "Backoffice" policy and BackofficeAuthorizationMiddleware pass unchanged, plus
/// client_id and auth_type=service for auditing. The human operator behind the
/// request (if any) travels in X-Acting-Admin and is surfaced as an acting_admin claim.
/// </summary>
public class ServiceApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IOptionsMonitor<ServiceClientsOptions> _serviceClients;

    public ServiceApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptionsMonitor<ServiceClientsOptions> serviceClients)
        : base(options, logger, encoder)
    {
        _serviceClients = serviceClients;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ServiceApiKeyDefaults.ApiKeyHeader, out var apiKeyValues))
        {
            // Not an API-key request — let other schemes handle it.
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var apiKey = apiKeyValues.FirstOrDefault();
        var clientId = Request.Headers[ServiceApiKeyDefaults.ClientIdHeader].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(clientId))
        {
            Logger.LogWarning("Service API key auth failed: missing {ApiKeyHeader} or {ClientIdHeader}",
                ServiceApiKeyDefaults.ApiKeyHeader, ServiceApiKeyDefaults.ClientIdHeader);
            return Task.FromResult(AuthenticateResult.Fail("Missing service credentials"));
        }

        var client = _serviceClients.CurrentValue.Clients
            .FirstOrDefault(c => string.Equals(c.ClientId, clientId, StringComparison.Ordinal));

        if (client == null || client.ApiKeyHashes.Count == 0 || string.IsNullOrWhiteSpace(client.ServiceUserId))
        {
            Logger.LogWarning("Service API key auth failed: unknown or misconfigured client {ClientId}", clientId);
            return Task.FromResult(AuthenticateResult.Fail("Invalid service credentials"));
        }

        var presentedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
        var presentedBytes = Encoding.UTF8.GetBytes(presentedHash);

        var matched = client.ApiKeyHashes.Any(expected =>
        {
            var expectedBytes = Encoding.UTF8.GetBytes(expected.Trim().ToLowerInvariant());
            return expectedBytes.Length == presentedBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(expectedBytes, presentedBytes);
        });

        if (!matched)
        {
            Logger.LogWarning("Service API key auth failed: key mismatch for client {ClientId}", clientId);
            return Task.FromResult(AuthenticateResult.Fail("Invalid service credentials"));
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, client.ServiceUserId),
            new(ClaimTypes.Name, client.ClientId),
            new(ClaimTypes.Role, "SuperAdmin"),
            new("role", "SuperAdmin"),
            new("is_backoffice", "true"),
            new("client_id", client.ClientId),
            new("auth_type", "service"),
        };

        var actingAdmin = Request.Headers[ServiceApiKeyDefaults.ActingAdminHeader].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(actingAdmin))
        {
            claims.Add(new Claim("acting_admin", actingAdmin.Trim()));
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);

        Logger.LogInformation("Service client {ClientId} authenticated (acting admin: {ActingAdmin})",
            client.ClientId, string.IsNullOrWhiteSpace(actingAdmin) ? "-" : actingAdmin);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Response.WriteAsJsonAsync(new { success = false, message = "Invalid service credentials" });
    }
}
