using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Notification.Domain;
using BeeLogistics.Modules.Notification.Infrastructure;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Notification.Infrastructure.Services;

/// <summary>
/// Email service that queues emails via Hangfire instead of sending immediately
/// </summary>
public class EmailQueueService : IEmailService
{
    private readonly NotificationDbContext _context;
    private readonly ILogger<EmailQueueService> _logger;
    private readonly IBackgroundJobClient _backgroundJobClient;

    public EmailQueueService(
        NotificationDbContext context,
        ILogger<EmailQueueService> logger,
        IBackgroundJobClient backgroundJobClient)
    {
        _context = context;
        _logger = logger;
        _backgroundJobClient = backgroundJobClient;
    }

    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        // Create email record
        var emailRecord = new EmailRecord
        {
            Id = Guid.NewGuid(),
            To = message.To,
            Subject = message.Subject,
            From = message.From,
            FromName = message.FromName,
            Status = EmailStatus.Queued,
            RetryCount = 0,
            CreatedAt = DateTime.UtcNow
        };

        _context.EmailRecords.Add(emailRecord);
        await _context.SaveChangesAsync(ct);

        // Queue email sending job with retry policy
        // Hangfire will process jobs from the "emails" queue (configured in Program.cs)
        var jobId = _backgroundJobClient.Enqueue<EmailSendingJob>(
            job => job.SendEmailAsync(emailRecord.Id, message, CancellationToken.None));

        // Update record with job ID
        emailRecord.HangfireJobId = jobId;
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Email queued for {To} with subject: {Subject} (JobId: {JobId})", 
            message.To, message.Subject, jobId);
    }

    public async Task SendTemplatedAsync(string templateName, EmailMessage message, Dictionary<string, string> placeholders, CancellationToken ct = default)
    {
        // Process template first, then queue
        var body = message.Body;
        foreach (var (key, value) in placeholders)
        {
            body = body.Replace($"{{{{{key}}}}}", value);
        }

        await SendAsync(message with { Body = body }, ct);
    }
}
