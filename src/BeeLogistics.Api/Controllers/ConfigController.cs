using System.Security.Claims;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Application.Options;
using BeeLogistics.Shared.Presentation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Api.Controllers;

// Any authenticated user (Customer, Driver, Owner, …) — both the customer and driver
// apps fetch their maps/MQTT config here, so it must not be role-restricted.
[Authorize]
public class ConfigController : BaseController
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConfigController> _logger;
    private readonly string _activePaymentGateway;

    public ConfigController(IConfiguration configuration, ILogger<ConfigController> logger, IOptions<PaymentGatewayOptions> paymentGatewayOptions)
    {
        _configuration = configuration;
        _logger = logger;
        _activePaymentGateway = PaymentProviders.Normalize(paymentGatewayOptions.Value.ActiveGateway) ?? PaymentProviders.PayMongo;
    }

    [HttpGet]
    public IActionResult Get()
    {
        // The Vault "driver-config" secret loads into IConfiguration with "__"→":" (e.g.
        // "DriverConfig__Mqtt__Host" → "DriverConfig:Mqtt:Host").
        //
        // MQTT is served EXCLUSIVELY from the Vault driver-config secret — NO fallback to the
        // backend's own appsettings "Mqtt:*" section. If Vault doesn't have it, it returns null.
        string? Mqtt(string key) => _configuration[$"DriverConfig:Mqtt:{key}"];
        int? MqttInt(string key) => _configuration.GetValue<int?>($"DriverConfig:Mqtt:{key}");
        bool? MqttBool(string key) => _configuration.GetValue<bool?>($"DriverConfig:Mqtt:{key}");

        // Maps: prefer the Vault-prefixed key, fall back to the bare key (no appsettings section
        // exists for Maps, so this just tolerates either secret-key naming).
        string? Maps(string key) => _configuration[$"DriverConfig:Maps:{key}"] ?? _configuration[$"Maps:{key}"];

        var mqttHost = Mqtt("Host");
        var googleKey = Maps("GoogleMapsApiKey");
        var mapboxToken = Maps("MapboxAccessToken");

        var role = User.FindFirst(ClaimTypes.Role)?.Value ?? User.FindFirst("role")?.Value ?? "unknown";

        // Log on hit — without dumping secret values (only presence/length).
        _logger.LogInformation(
            "[ConfigController] GET /api/config hit (role={Role}). Resolved: mqtt_host={MqttHost}, mqtt_use_ssl={Ssl}, google_maps_api_key={Google}, mapbox_access_token={Mapbox}",
            role,
            mqttHost ?? "(null)",
            MqttBool("UseSsl"),
            googleKey is null ? "(empty/missing)" : $"set(len={googleKey.Length})",
            mapboxToken is null ? "(empty/missing)" : $"set(len={mapboxToken.Length})");

        if (mapboxToken is null || googleKey is null || mqttHost is null)
        {
            _logger.LogWarning(
                "[ConfigController] One or more values are empty — check the Vault 'driver-config' secret (keys DriverConfig__Maps__* / DriverConfig__Mqtt__*) and the AppRole read policy.");
        }

        return Ok(new
        {
            success = true,
            data = new
            {
                mqtt_host         = mqttHost,
                mqtt_port         = MqttInt("Port"),
                mqtt_username     = Mqtt("Username"),
                mqtt_password     = Mqtt("Password"),
                mqtt_use_ssl      = MqttBool("UseSsl"),
                mqtt_topic_prefix = Mqtt("TopicPrefix"),
                mqtt_path         = Mqtt("Path"),
                google_maps_api_key    = googleKey,
                mapbox_access_token    = mapboxToken,
                // Which provider SDK the client must tokenize with (Xendit.js vs
                // PayMongo.js) and whose checkout flow new payments use.
                payment_gateway        = _activePaymentGateway,
            }
        });
    }
}
