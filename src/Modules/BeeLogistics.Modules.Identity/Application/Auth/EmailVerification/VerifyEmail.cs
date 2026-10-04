using BeeLogistics.Modules.Identity.Application;
using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Notification.Application.Services;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using System.Text;

namespace BeeLogistics.Modules.Identity.Application.Auth.EmailVerification;

/// <summary>Confirm an email address using the token from the verification link.</summary>
public record VerifyEmailCommand(string? Email, string? Token) : IRequest<MessageOutcome>;

public class VerifyEmailCommandHandler : IRequestHandler<VerifyEmailCommand, MessageOutcome>
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IEmailService _emailService;
    private readonly IEmailTemplateService _emailTemplateService;
    private readonly IAuditService _auditService;
    private readonly ILogger<VerifyEmailCommandHandler>? _logger;

    public VerifyEmailCommandHandler(
        UserManager<ApplicationUser> userManager,
        IEmailService emailService,
        IEmailTemplateService emailTemplateService,
        IAuditService auditService,
        ILogger<VerifyEmailCommandHandler>? logger = null)
    {
        _userManager = userManager;
        _emailService = emailService;
        _emailTemplateService = emailTemplateService;
        _auditService = auditService;
        _logger = logger;
    }

    public async Task<MessageOutcome> Handle(VerifyEmailCommand request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Token))
        {
            return MessageOutcome.Fail("Email and token are required");
        }

        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user == null)
        {
            return MessageOutcome.Fail("Invalid verification link");
        }

        if (user.EmailConfirmed)
        {
            return MessageOutcome.Ok("Email is already verified");
        }

        try
        {
            var decodedToken = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(request.Token));
            var result = await _userManager.ConfirmEmailAsync(user, decodedToken);

            if (!result.Succeeded)
            {
                var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                _logger?.LogWarning("Email verification failed for {Email}: {Errors}", request.Email, errors);
                return MessageOutcome.Fail("Invalid or expired verification token");
            }

            // Send welcome email
            try
            {
                var welcomeEmail = _emailTemplateService.CreateWelcomeEmail(user.Email!, user.FullName);
                await _emailService.SendAsync(welcomeEmail);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to send welcome email to {Email}", user.Email);
                // Don't fail verification if welcome email fails
            }

            await _auditService.LogAsync(AuditActions.EmailVerified, AuditCategories.Auth,
                userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role);

            return MessageOutcome.Ok("Email verified successfully");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error verifying email for {Email}", request.Email);
            return MessageOutcome.Fail("Invalid verification token");
        }
    }
}
