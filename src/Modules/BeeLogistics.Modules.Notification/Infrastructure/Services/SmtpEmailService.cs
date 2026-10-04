using BeeLogistics.Modules.Notification.Application.Interfaces;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace BeeLogistics.Modules.Notification.Infrastructure.Services;

public class SmtpEmailService : IEmailService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<SmtpEmailService> _logger;

    public SmtpEmailService(IConfiguration configuration, ILogger<SmtpEmailService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var emailSettings = _configuration.GetSection("Email");
        
        var email = new MimeMessage();
        
        // Get sender email and name
        var fromEmail = message.From ?? emailSettings["From"] ?? throw new InvalidOperationException("Email:From not configured");
        var fromName = message.FromName ?? emailSettings["FromName"] ?? "My Bee App On-Demand";
        email.From.Add(new MailboxAddress(fromName, fromEmail));
        
        email.To.Add(MailboxAddress.Parse(message.To));
        
        if (message.Cc?.Any() == true)
            email.Cc.AddRange(message.Cc.Select(MailboxAddress.Parse));
        
        if (message.Bcc?.Any() == true)
            email.Bcc.AddRange(message.Bcc.Select(MailboxAddress.Parse));

        email.Subject = message.Subject;
        email.Body = new TextPart(message.IsHtml ? "html" : "plain") { Text = message.Body };

        using var smtp = new SmtpClient();
        
        // Set timeout to 30 seconds (default is 120 seconds which is too long)
        smtp.Timeout = 30000;
        
        var host = emailSettings["Host"] ?? throw new InvalidOperationException("Email:Host not configured");
        var port = int.Parse(emailSettings["Port"] ?? "587");
        var username = emailSettings["Username"];
        var password = emailSettings["Password"];
        var useSsl = bool.Parse(emailSettings["UseSsl"] ?? "true");

        _logger.LogInformation("Sending email to {To} with subject: {Subject}", message.To, message.Subject);
        _logger.LogInformation("Connecting to SMTP server {Host}:{Port}", host, port);

        try
        {
            // Use Auto to automatically detect the correct SSL/TLS option based on server capabilities
            // This works for both port 465 (implicit SSL) and 587 (StartTLS)
            await smtp.ConnectAsync(host, port, SecureSocketOptions.Auto, ct);
            _logger.LogInformation("Successfully connected to SMTP server");
            
            if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
            {
                await smtp.AuthenticateAsync(username, password, ct);
                _logger.LogInformation("Successfully authenticated with SMTP server");
            }

            await smtp.SendAsync(email, ct);
            await smtp.DisconnectAsync(true, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SMTP error: {Message}", ex.Message);
            throw;
        }

        _logger.LogInformation("Email sent successfully to {To}", message.To);
    }

    public async Task SendTemplatedAsync(string templateName, EmailMessage message, Dictionary<string, string> placeholders, CancellationToken ct = default)
    {
        var body = message.Body;
        foreach (var (key, value) in placeholders)
        {
            body = body.Replace($"{{{{{key}}}}}", value);
        }

        await SendAsync(message with { Body = body }, ct);
    }
}
