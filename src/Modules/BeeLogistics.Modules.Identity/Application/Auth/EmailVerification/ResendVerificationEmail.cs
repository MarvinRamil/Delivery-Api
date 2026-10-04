using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Notification.Application.Services;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using System.Text;

namespace BeeLogistics.Modules.Identity.Application.Auth.EmailVerification;

/// <summary>Reissue the email-confirmation link.</summary>
public record ResendVerificationEmailCommand(string? Email) : IRequest<MessageOutcome>;

public class ResendVerificationEmailCommandHandler : IRequestHandler<ResendVerificationEmailCommand, MessageOutcome>
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IEmailService _emailService;
    private readonly IEmailTemplateService _emailTemplateService;
    private readonly ILogger<ResendVerificationEmailCommandHandler>? _logger;

    public ResendVerificationEmailCommandHandler(
        UserManager<ApplicationUser> userManager,
        IEmailService emailService,
        IEmailTemplateService emailTemplateService,
        ILogger<ResendVerificationEmailCommandHandler>? logger = null)
    {
        _userManager = userManager;
        _emailService = emailService;
        _emailTemplateService = emailTemplateService;
        _logger = logger;
    }

    public async Task<MessageOutcome> Handle(ResendVerificationEmailCommand request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return MessageOutcome.Fail(AuthMessages.EmailRequired);
        }

        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user == null)
        {
            // Don't reveal if email exists for security
            return MessageOutcome.Ok("If the email exists and is not verified, a verification email has been sent.");
        }

        if (user.EmailConfirmed)
        {
            return MessageOutcome.Ok("Email is already verified");
        }

        try
        {
            var emailToken = await _userManager.GenerateEmailConfirmationTokenAsync(user);
            var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(emailToken));

            var verificationEmail = _emailTemplateService.CreateVerificationEmail(
                user.Email!,
                user.FullName,
                encodedToken
            );

            await _emailService.SendAsync(verificationEmail);
            _logger?.LogInformation("Verification email resent to {Email}", user.Email);

            return MessageOutcome.Ok("Verification email sent. Please check your inbox.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to resend verification email to {Email}", request.Email);
            return MessageOutcome.Fail("Failed to send verification email. Please try again later.");
        }
    }
}
