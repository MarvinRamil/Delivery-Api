using BeeLogistics.Modules.Notification.Application.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BeeLogistics.Tests.Notifications;

public class DriverApplicationEmailTemplateTests
{
    private static EmailTemplateService NewService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:BaseUrl"] = "https://test.mybeeapp.com"
            })
            .Build();
        return new EmailTemplateService(configuration);
    }

    [Fact]
    public void Approved_email_addresses_driver_and_mentions_active_account()
    {
        var email = NewService().CreateDriverApplicationApprovedEmail("driver@example.com", "Juan Dela Cruz");

        Assert.Equal("driver@example.com", email.To);
        Assert.True(email.IsHtml);
        Assert.Contains("Driver Application Approved", email.Subject);
        Assert.Contains("My Bee App On-Demand", email.Subject);
        Assert.Contains("Juan Dela Cruz", email.Body);
        Assert.Contains("approved", email.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("accepting bookings", email.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{FullName}", email.Body);
        Assert.DoesNotContain("{Year}", email.Body);
    }

    [Fact]
    public void Rejected_email_includes_reason_when_notes_provided()
    {
        var email = NewService().CreateDriverApplicationRejectedEmail(
            "driver@example.com", "Juan Dela Cruz", "Expired driver's license");

        Assert.Equal("driver@example.com", email.To);
        Assert.True(email.IsHtml);
        Assert.Contains("Driver Application Update", email.Subject);
        Assert.Contains("Juan Dela Cruz", email.Body);
        Assert.Contains("Reason", email.Body);
        // HTML-encoded because notes are admin free text
        Assert.Contains("Expired driver&#39;s license", email.Body);
        Assert.Contains("new application", email.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{ReasonBlock}", email.Body);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejected_email_omits_reason_when_notes_missing(string? notes)
    {
        var email = NewService().CreateDriverApplicationRejectedEmail(
            "driver@example.com", "Juan Dela Cruz", notes);

        Assert.DoesNotContain("Reason", email.Body);
        Assert.DoesNotContain("{ReasonBlock}", email.Body);
        Assert.Contains("Juan Dela Cruz", email.Body);
    }

    [Fact]
    public void Rejected_email_html_encodes_notes()
    {
        var email = NewService().CreateDriverApplicationRejectedEmail(
            "driver@example.com", "Juan Dela Cruz", "<script>alert(1)</script>");

        Assert.DoesNotContain("<script>", email.Body);
        Assert.Contains("&lt;script&gt;", email.Body);
    }
}
