using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Notification.Domain;
using BeeLogistics.Modules.Notification.Infrastructure;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Notification.Infrastructure.Services;

/// <summary>
/// Hangfire background job for sending emails
/// </summary>
public class EmailSendingJob
{
    private readonly NotificationDbContext _context;
    private readonly SmtpEmailService _smtpEmailService;
    private readonly ILogger<EmailSendingJob> _logger;

    public EmailSendingJob(
        NotificationDbContext context,
        SmtpEmailService smtpEmailService,
        ILogger<EmailSendingJob> logger)
    {
        _context = context;
        _smtpEmailService = smtpEmailService;
        _logger = logger;
    }

    /// <summary>
    /// Send email via SMTP service and update status
    /// This method is called by Hangfire with automatic retry
    /// </summary>
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 900 })] // 1min, 5min, 15min
    public async Task SendEmailAsync(Guid emailRecordId, EmailMessage message, CancellationToken ct)
    {
        var emailRecord = await _context.EmailRecords.FindAsync(new object[] { emailRecordId }, ct);
        if (emailRecord == null)
        {
            _logger.LogError("Email record {EmailRecordId} not found", emailRecordId);
            return;
        }

        try
        {
            // Update status to Processing
            emailRecord.Status = EmailStatus.Processing;
            emailRecord.RetryCount++;
            await _context.SaveChangesAsync(ct);

            _logger.LogInformation("Processing email {EmailRecordId} to {To} (Attempt {RetryCount})", 
                emailRecordId, message.To, emailRecord.RetryCount);

            // Send email via SMTP service
            await _smtpEmailService.SendAsync(message, ct);

            // Update status to Sent
            emailRecord.Status = EmailStatus.Sent;
            emailRecord.SentAt = DateTime.UtcNow;
            emailRecord.ErrorMessage = null;
            await _context.SaveChangesAsync(ct);

            _logger.LogInformation("Email {EmailRecordId} sent successfully to {To}", 
                emailRecordId, message.To);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email {EmailRecordId} to {To}", 
                emailRecordId, message.To);

            // Update status - Hangfire will retry if attempts < 3
            emailRecord.Status = EmailStatus.Failed;
            emailRecord.ErrorMessage = ex.Message.Length > 2000 ? ex.Message.Substring(0, 2000) : ex.Message;
            emailRecord.FailedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);

            // Re-throw to trigger Hangfire retry
            throw;
        }
    }

    /// <summary>
    /// Called by Hangfire when all retries are exhausted
    /// Moves email to DeadLetter status
    /// </summary>
    [JobDisplayName("Email Dead Letter Handler")]
    public async Task HandleDeadLetterAsync(Guid emailRecordId, CancellationToken ct)
    {
        var emailRecord = await _context.EmailRecords.FindAsync(new object[] { emailRecordId }, ct);
        if (emailRecord == null)
        {
            _logger.LogError("Email record {EmailRecordId} not found for dead letter handling", emailRecordId);
            return;
        }

        emailRecord.Status = EmailStatus.DeadLetter;
        emailRecord.FailedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        _logger.LogWarning("Email {EmailRecordId} moved to dead letter queue after {RetryCount} attempts", 
            emailRecordId, emailRecord.RetryCount);
    }
}
