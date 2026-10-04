using BeeLogistics.Modules.Identity.Application;
using BeeLogistics.Modules.Identity.Application.Services;
using BeeLogistics.Modules.Identity.Domain;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Identity.Application.Auth.Password;

/// <summary>One of the user's configured security questions, as returned to the client.</summary>
public sealed record SecurityQuestionView(int Number, int QuestionId, string Question);

/// <summary>
/// Outcome of a security-questions lookup. Success renders <c>{ success, questions }</c>;
/// failure renders <c>{ success, message }</c> - the two bodies do not share a shape, so they
/// are rendered separately at the endpoint.
/// </summary>
public sealed record SecurityQuestionsOutcome(bool Success, string? Message, IReadOnlyList<SecurityQuestionView> Questions)
{
    public static SecurityQuestionsOutcome Fail(string message) => new(false, message, Array.Empty<SecurityQuestionView>());
    public static SecurityQuestionsOutcome Ok(IReadOnlyList<SecurityQuestionView> questions) => new(true, null, questions);
}

/// <summary>
/// The security questions a user must answer to reset their password.
/// SECURITY: an unknown or inactive account returns an empty list rather than an error, so the
/// response cannot be used to probe which addresses exist.
/// </summary>
public record GetForgotPasswordQuestionsQuery(string? Email) : IRequest<SecurityQuestionsOutcome>;

public class GetForgotPasswordQuestionsQueryHandler : IRequestHandler<GetForgotPasswordQuestionsQuery, SecurityQuestionsOutcome>
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAuditService _auditService;
    private readonly ILogger<GetForgotPasswordQuestionsQueryHandler>? _logger;

    public GetForgotPasswordQuestionsQueryHandler(
        UserManager<ApplicationUser> userManager,
        IAuditService auditService,
        ILogger<GetForgotPasswordQuestionsQueryHandler>? logger = null)
    {
        _userManager = userManager;
        _auditService = auditService;
        _logger = logger;
    }

    public async Task<SecurityQuestionsOutcome> Handle(GetForgotPasswordQuestionsQuery request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return SecurityQuestionsOutcome.Fail(AuthMessages.EmailRequired);
        }

        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user == null || !user.IsActive)
        {
            if (user == null)
            {
                _logger?.LogWarning("Security questions requested for unknown email {Email}", request.Email);
                await _auditService.LogAsync(AuditActions.PasswordResetRequested, AuditCategories.Auth,
                    userEmail: request.Email,
                    isSuccess: false, errorMessage: "Security questions requested for unknown email");
            }
            else
            {
                _logger?.LogWarning("Security questions requested for inactive account {UserId}", user.Id);
                await _auditService.LogAsync(AuditActions.PasswordResetRequested, AuditCategories.Auth,
                    userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role,
                    isSuccess: false, errorMessage: "Security questions requested for inactive account");
            }
            // Don't reveal if email exists for security
            return SecurityQuestionsOutcome.Ok(Array.Empty<SecurityQuestionView>());
        }

        var questions = new List<SecurityQuestionView>();
        AddIfConfigured(questions, 1, user.SecurityQuestionId1);
        AddIfConfigured(questions, 2, user.SecurityQuestionId2);
        AddIfConfigured(questions, 3, user.SecurityQuestionId3);

        return SecurityQuestionsOutcome.Ok(questions);
    }

    private static void AddIfConfigured(List<SecurityQuestionView> into, int number, int? questionId)
    {
        if (!questionId.HasValue) return;

        var question = SecurityQuestionsService.GetQuestionById(questionId.Value);
        if (question == null) return;

        into.Add(new SecurityQuestionView(number, question.Id, question.Question));
    }
}
