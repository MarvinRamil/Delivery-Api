namespace BeeLogistics.Modules.Map.Application.Services;

public static class MqttTopicConvention
{
    public static string NormalizeEnvironment(string? rawEnvironment)
    {
        if (string.IsNullOrWhiteSpace(rawEnvironment))
        {
            return "dev";
        }

        var value = rawEnvironment.Trim().ToLowerInvariant();
        return value switch
        {
            "development" => "dev",
            "dev" => "dev",
            "staging" => "staging",
            "uat" => "staging",
            "production" => "prod",
            "prod" => "prod",
            _ => value
        };
    }

    public static string BuildLocationTopicPattern(string mqttEnvironment)
    {
        var env = NormalizeEnvironment(mqttEnvironment);
        return env == "prod"
            ? "beelogistics/drivers/+/location"
            : $"{env}/beelogistics/drivers/+/location";
    }

    public static string BuildDriverLocationTopic(Guid driverId, string mqttEnvironment)
    {
        var env = NormalizeEnvironment(mqttEnvironment);
        return env == "prod"
            ? $"beelogistics/drivers/{driverId}/location"
            : $"{env}/beelogistics/drivers/{driverId}/location";
    }
}

