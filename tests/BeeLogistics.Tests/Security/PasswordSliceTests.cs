using BeeLogistics.Modules.Identity.Application;
using BeeLogistics.Modules.Identity.Application.Auth;
using BeeLogistics.Modules.Identity.Application.Auth.Password;
using BeeLogistics.Modules.Identity.Application.Services;
using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Notification.Application.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Security;

/// <summary>
/// Behaviour of the password vertical slice (issue #45, stage 3).
///
/// The properties worth protecting here are all anti-disclosure: an unknown address, an inactive
/// account and a wrong security answer must be indistinguishable from success on the
/// forgot-password paths, and a wrong answer must NOT be indistinguishable on reset-password
/// (where the caller already proved control of the mailbox). Those two rules differ deliberately
/// and are easy to break while moving code, so they are pinned.
/// </summary>
public class PasswordSliceTests
{
    private const string CorrectAnswer = "fluffy";

    private static OtpService NewOtpService()
        => new(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));

    private static UserManager<ApplicationUser> NewUserManager(ApplicationUser? byEmail = null)
    {
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        var mgr = Substitute.For<UserManager<ApplicationUser>>(
            store, null!, null!, null!, null!, null!, null!, null!, null!);
        mgr.FindByEmailAsync(Arg.Any<string>()).Returns(byEmail);
        return mgr;
    }

    private static ApplicationUser UserWithQuestion(bool active = true) => new()
    {
        Id = "u1",
        Email = "user@example.com",
        UserName = "user@example.com",
        FullName = "Test User",
        Role = "Customer",
        IsActive = active,
        SecurityQuestionId1 = 1,
        SecurityAnswerHash1 = AuthHelpers.HashSecurityAnswer(CorrectAnswer)
    };

    private static ForgotPasswordCommandHandler NewForgotHandler(UserManager<ApplicationUser> mgr, IEmailService? email = null)
        => new(mgr, Substitute.For<IAuditService>(), email ?? Substitute.For<IEmailService>(),
               Substitute.For<IEmailTemplateService>());

    private static List<SecurityAnswerInput> Answers(int number, string answer)
        => new() { new SecurityAnswerInput(number, answer) };

    // --- forgot-password: everything looks the same from outside ------------

    [Fact]
    public async Task Forgot_password_requires_an_email()
    {
        var outcome = await NewForgotHandler(NewUserManager()).Handle(new ForgotPasswordCommand("  ", null), default);

        Assert.False(outcome.Success);
        Assert.Equal("Email is required", outcome.Message);
    }

    [Fact]
    public async Task Forgot_password_answers_identically_for_unknown_and_inactive_accounts()
    {
        var email = Substitute.For<IEmailService>();

        var unknown = await NewForgotHandler(NewUserManager(null), email)
            .Handle(new ForgotPasswordCommand("nobody@example.com", null), default);
        var inactive = await NewForgotHandler(NewUserManager(UserWithQuestion(active: false)), email)
            .Handle(new ForgotPasswordCommand("user@example.com", null), default);

        Assert.True(unknown.Success);
        Assert.True(inactive.Success);
        Assert.Equal(unknown.Message, inactive.Message);
        // Neither may trigger a reset mail.
        await email.DidNotReceive().SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Forgot_password_hides_a_wrong_security_answer_behind_the_generic_message()
    {
        var email = Substitute.For<IEmailService>();
        var handler = NewForgotHandler(NewUserManager(UserWithQuestion()), email);

        var outcome = await handler.Handle(
            new ForgotPasswordCommand("user@example.com", Answers(1, "wrong")), default);

        // Reported as success so the endpoint cannot be used to brute-force security answers...
        Assert.True(outcome.Success);
        // ...but no reset link is actually sent.
        await email.DidNotReceive().SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Forgot_password_rejects_a_missing_answer_when_questions_are_configured()
    {
        var handler = NewForgotHandler(NewUserManager(UserWithQuestion()));

        var outcome = await handler.Handle(new ForgotPasswordCommand("user@example.com", null), default);

        Assert.False(outcome.Success);
        Assert.Equal("At least one security answer is required", outcome.Message);
    }

    // --- reset-password: a wrong answer IS reported ------------------------

    private static ResetPasswordCommandHandler NewResetHandler(UserManager<ApplicationUser> mgr)
        => new(mgr, NewOtpService(), Substitute.For<IAuditService>(),
               Substitute.For<ITokenBlacklistService>(), Substitute.For<IRefreshTokenService>(),
               Substitute.For<IEmailService>(), Substitute.For<IEmailTemplateService>());

    [Theory]
    [InlineData(null, "Email and new password are required")]
    [InlineData("short", "Password must be at least 8 characters")]
    public async Task Reset_password_validates_the_new_password(string? newPassword, string expected)
    {
        var handler = NewResetHandler(NewUserManager(UserWithQuestion()));

        var outcome = await handler.Handle(
            new ResetPasswordCommand("user@example.com", "tok", null, newPassword, null), default);

        Assert.False(outcome.Success);
        Assert.Equal(expected, outcome.Message);
    }

    [Fact]
    public async Task Reset_password_requires_either_a_token_or_an_otp()
    {
        var handler = NewResetHandler(NewUserManager(UserWithQuestion()));

        var outcome = await handler.Handle(
            new ResetPasswordCommand("user@example.com", null, null, "ValidPass1!", null), default);

        Assert.False(outcome.Success);
        Assert.Equal("Either token or OTP is required", outcome.Message);
    }

    [Fact]
    public async Task Reset_password_reports_a_wrong_security_answer_plainly()
    {
        // Unlike forgot-password, the caller here already holds a token or OTP, so telling them
        // the answer was wrong leaks nothing they could not already determine.
        var handler = NewResetHandler(NewUserManager(UserWithQuestion()));

        var outcome = await handler.Handle(
            new ResetPasswordCommand("user@example.com", "tok", null, "ValidPass1!", Answers(1, "wrong")), default);

        Assert.False(outcome.Success);
        Assert.Equal("Invalid security answer(s). Please check your answers and try again.", outcome.Message);
    }

    [Fact]
    public async Task Reset_password_requires_answers_when_questions_are_configured()
    {
        var handler = NewResetHandler(NewUserManager(UserWithQuestion()));

        var outcome = await handler.Handle(
            new ResetPasswordCommand("user@example.com", "tok", null, "ValidPass1!", null), default);

        Assert.False(outcome.Success);
        Assert.Equal("Security answers are required. Please answer at least one security question.", outcome.Message);
    }

    [Fact]
    public async Task Reset_password_hides_an_unknown_address_behind_a_neutral_error()
    {
        var handler = NewResetHandler(NewUserManager(null));

        var outcome = await handler.Handle(
            new ResetPasswordCommand("nobody@example.com", "tok", null, "ValidPass1!", null), default);

        Assert.False(outcome.Success);
        Assert.Equal("Invalid request", outcome.Message);
    }

    [Fact]
    public async Task Reset_password_refuses_a_deactivated_account()
    {
        var handler = NewResetHandler(NewUserManager(UserWithQuestion(active: false)));

        var outcome = await handler.Handle(
            new ResetPasswordCommand("user@example.com", "tok", null, "ValidPass1!", null), default);

        Assert.False(outcome.Success);
        Assert.Equal("Account is deactivated", outcome.Message);
    }

    // --- security answer matching ------------------------------------------

    [Fact]
    public async Task A_correct_answer_to_any_configured_question_is_enough()
    {
        var user = UserWithQuestion();
        user.SecurityQuestionId2 = 2;
        user.SecurityAnswerHash2 = AuthHelpers.HashSecurityAnswer("second");
        var email = Substitute.For<IEmailService>();
        var handler = NewForgotHandler(NewUserManager(user), email);

        // Answer only question 2, leaving question 1 unanswered.
        var outcome = await handler.Handle(
            new ForgotPasswordCommand("user@example.com", Answers(2, "SECOND")), default);

        Assert.True(outcome.Success);
        // The reset mail really was sent, distinguishing this from the generic-failure path.
        await email.Received(1).SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Answers_are_matched_case_insensitively_and_trimmed()
    {
        var email = Substitute.For<IEmailService>();
        var handler = NewForgotHandler(NewUserManager(UserWithQuestion()), email);

        var outcome = await handler.Handle(
            new ForgotPasswordCommand("user@example.com", Answers(1, "  FLUFFY  ")), default);

        Assert.True(outcome.Success);
        await email.Received(1).SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_answer_aimed_at_an_unconfigured_question_number_does_not_match()
    {
        var email = Substitute.For<IEmailService>();
        var handler = NewForgotHandler(NewUserManager(UserWithQuestion()), email);

        // Question 3 is not configured; the correct answer text must not be accepted for it.
        var outcome = await handler.Handle(
            new ForgotPasswordCommand("user@example.com", Answers(3, CorrectAnswer)), default);

        Assert.True(outcome.Success); // generic message
        await email.DidNotReceive().SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    // --- change password ----------------------------------------------------

    [Fact]
    public async Task Request_password_change_otp_reports_a_missing_user_as_not_found()
    {
        var handler = new RequestPasswordChangeOtpCommandHandler(
            NewUserManager(), NewOtpService(), Substitute.For<IEmailService>());

        var outcome = await handler.Handle(new RequestPasswordChangeOtpCommand("missing"), default);

        // Renders as NotFound("User not found") - a bare string body, not the JSON envelope.
        Assert.Equal(AuthOutcomeStatus.NotFound, outcome.Status);
        Assert.Equal("User not found", outcome.Message);
    }

    [Fact]
    public async Task Change_password_requires_an_otp_or_the_current_password()
    {
        var user = UserWithQuestion();
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        var mgr = Substitute.For<UserManager<ApplicationUser>>(
            store, null!, null!, null!, null!, null!, null!, null!, null!);
        mgr.FindByIdAsync("u1").Returns(user);

        var handler = new ChangePasswordCommandHandler(
            mgr, NewOtpService(), Substitute.For<IAuditService>(),
            Substitute.For<ITokenBlacklistService>(), Substitute.For<IRefreshTokenService>(),
            Substitute.For<IEmailService>(), Substitute.For<IEmailTemplateService>());

        var outcome = await handler.Handle(
            new ChangePasswordCommand("u1", CurrentPassword: null, NewPassword: "ValidPass1!", Otp: null), default);

        Assert.False(outcome.Success);
        Assert.Equal("Either OTP or current password is required", outcome.Message);
    }
}
