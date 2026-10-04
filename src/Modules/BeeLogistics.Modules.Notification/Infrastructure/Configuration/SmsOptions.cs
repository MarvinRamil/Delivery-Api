namespace BeeLogistics.Modules.Notification.Infrastructure.Configuration;

public class SmsOptions
{
    public const string SectionName = "Sms";

    public List<string> ProviderOrder { get; set; } = ["IlocosSms", "PhilSMS"];

    public string? ForceProvider { get; set; }

    public SmsProvidersOptions Providers { get; set; } = new();
}

public class SmsProvidersOptions
{
    public IlocosSmsProviderOptions IlocosSms { get; set; } = new();

    public PhilSmsProviderOptions PhilSMS { get; set; } = new();
}

public class IlocosSmsProviderOptions
{
    public string ApiBaseUrl { get; set; } = "https://sms.ilocosscript.live";

    public string ApiKey { get; set; } = string.Empty;
}

public class PhilSmsProviderOptions
{
    public string ApiBaseUrl { get; set; } = "https://dashboard.philsms.com/api/v3";

    public string ApiToken { get; set; } = string.Empty;

    public string SenderId { get; set; } = "BeeOnDemand";
}
