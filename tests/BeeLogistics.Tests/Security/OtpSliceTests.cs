using BeeLogistics.Modules.Identity.Application.Auth.Otp;
using BeeLogistics.Modules.Identity.Application.Services;
using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Modules.Identity.Infrastructure;
using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Notification.Application.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Security;

/// <summary>
/// Behaviour of the OTP vertical slice (issue #45, stage 3 pilot).
///
/// These use a REAL <see cref="OtpService"/> over an in-memory distributed cache rather than a
/// mock, so the generate/validate/lock semantics under test are the ones that actually run.
///
/// The security-critical property here is anti-enumeration: an address that already exists, one
/// that does not, and a downstream send failure must all produce the same answer. That was true
/// of the controller code these handlers replaced, and these tests keep it true.
/// </summary>
public class OtpSliceTests
{
    private static OtpService NewOtpService()
        => new(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));

    private static UserManager<ApplicationUser> NewUserManager(ApplicationUser? existing = null)
    {
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        var mgr = Substitute.For<UserManager<ApplicationUser>>(
            store, null!, null!, null!, null!, null!, null!, null!, null!);
        mgr.FindByEmailAsync(Arg.Any<string>()).Returns(existing);
        return mgr;
    }

    private static IRegistrationVerificationTokenService NewTokens(string token = "tok.sig")
    {
        var t = Substitute.For<IRegistrationVerificationTokenService>();
        t.Create(Arg.Any<string>()).Returns(token);
        return t;
    }

    // --- send email otp -----------------------------------------------------

    [Fact]
    public async Task Send_email_otp_requires_an_email()
    {
        var handler = new SendEmailOtpCommandHandler(NewOtpService(), NewUserManager(), Substitute.For<IEmailService>());

        var outcome = await handler.Handle(new SendEmailOtpCommand("   "), default);

        Assert.False(outcome.Success);
        Assert.Equal("Email is required", outcome.Message);
    }

    [Fact]
    public async Task Send_email_otp_does_not_reveal_that_an_address_is_already_registered()
    {
        var email = Substitute.For<IEmailService>();
        var existing = new ApplicationUser { Id = "u1", Email = "taken@example.com", FullName = "Taken", Role = "Customer" };
        var handler = new SendEmailOtpCommandHandler(NewOtpService(), NewUserManager(existing), email);

        var outcome = await handler.Handle(new SendEmailOtpCommand("taken@example.com"), default);

        Assert.True(outcome.Success);
        Assert.Equal("If this email is registered, a verification code has been sent.", outcome.Message);
        // No OTP mail for an address that already exists.
        await email.DidNotReceive().SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Send_email_otp_sends_mail_for_a_new_address()
    {
        var email = Substitute.For<IEmailService>();
        var handler = new SendEmailOtpCommandHandler(NewOtpService(), NewUserManager(), email);

        var outcome = await handler.Handle(new SendEmailOtpCommand("New@Example.com"), default);

        Assert.True(outcome.Success);
        // Normalized to lowercase before sending.
        await email.Received(1).SendAsync(Arg.Is<EmailMessage>(m => m.To == "new@example.com"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Send_email_otp_reports_success_even_when_the_mail_provider_throws()
    {
        var email = Substitute.For<IEmailService>();
        email.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
             .Returns<Task>(_ => throw new InvalidOperationException("smtp down"));
        var handler = new SendEmailOtpCommandHandler(NewOtpService(), NewUserManager(), email);

        var outcome = await handler.Handle(new SendEmailOtpCommand("new@example.com"), default);

        // Anti-enumeration: a provider failure must look exactly like a success.
        Assert.True(outcome.Success);
        Assert.Equal("If this email is valid, a verification code has been sent. Please check your inbox.", outcome.Message);
    }

    // --- verify email otp ---------------------------------------------------

    [Fact]
    public async Task Verify_email_otp_requires_both_email_and_otp()
    {
        var handler = new VerifyEmailOtpCommandHandler(NewOtpService(), NewTokens());

        var outcome = await handler.Handle(new VerifyEmailOtpCommand("a@b.com", null), default);

        Assert.False(outcome.Success);
        Assert.Equal("Email and OTP are required", outcome.Message);
    }

    [Fact]
    public async Task Verify_email_otp_rejects_a_wrong_code_and_issues_no_token()
    {
        var otpService = NewOtpService();
        await otpService.GenerateEmailVerificationOtpAsync("a@b.com");
        var handler = new VerifyEmailOtpCommandHandler(otpService, NewTokens());

        var outcome = await handler.Handle(new VerifyEmailOtpCommand("a@b.com", "000000"), default);

        Assert.False(outcome.Success);
        Assert.Null(outcome.RegistrationToken);
    }

    [Fact]
    public async Task Verify_email_otp_accepts_the_real_code_and_returns_a_registration_token()
    {
        var otpService = NewOtpService();
        var otp = await otpService.GenerateEmailVerificationOtpAsync("a@b.com");
        var handler = new VerifyEmailOtpCommandHandler(otpService, NewTokens("issued-token"));

        var outcome = await handler.Handle(new VerifyEmailOtpCommand("A@B.com", otp), default);

        Assert.True(outcome.Success);
        Assert.Equal("Email verified. You can now complete registration.", outcome.Message);
        Assert.Equal("issued-token", outcome.RegistrationToken);
    }

    // --- phone number guards ------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Verify_sms_otp_requires_a_phone_number(string? phone)
    {
        var handler = new VerifySmsOtpCommandHandler(NewOtpService(), NewTokens());

        var outcome = await handler.Handle(new VerifySmsOtpCommand(phone, "123456"), default);

        Assert.False(outcome.Success);
        Assert.Equal("Phone number and OTP are required", outcome.Message);
    }

    [Theory]
    [InlineData("123")]              // too short once normalized
    [InlineData("0917")]             // too short
    [InlineData("091712345678901234567890")] // too long
    public async Task Verify_sms_otp_rejects_implausible_phone_numbers(string phone)
    {
        var handler = new VerifySmsOtpCommandHandler(NewOtpService(), NewTokens());

        var outcome = await handler.Handle(new VerifySmsOtpCommand(phone, "123456"), default);

        Assert.False(outcome.Success);
        Assert.Equal("Invalid phone number format", outcome.Message);
    }

    [Fact]
    public async Task Verify_sms_otp_accepts_the_real_code_and_returns_a_registration_token()
    {
        var otpService = NewOtpService();
        // 09171234567 normalizes to 639171234567
        var otp = await otpService.GeneratePhoneVerificationOtpAsync("639171234567");
        var handler = new VerifySmsOtpCommandHandler(otpService, NewTokens("phone-token"));

        var outcome = await handler.Handle(new VerifySmsOtpCommand("0917 123 4567", otp), default);

        Assert.True(outcome.Success);
        Assert.Equal("Phone verified. You can now complete registration.", outcome.Message);
        Assert.Equal("phone-token", outcome.RegistrationToken);
    }

    // --- resend -------------------------------------------------------------

    [Fact]
    public async Task Resend_email_otp_invalidates_the_previous_code()
    {
        var otpService = NewOtpService();
        var first = await otpService.GenerateEmailVerificationOtpAsync("a@b.com");
        var handler = new ResendEmailOtpCommandHandler(otpService, Substitute.For<IEmailService>());

        var outcome = await handler.Handle(new ResendEmailOtpCommand("a@b.com"), default);
        Assert.True(outcome.Success);

        // The superseded code must no longer verify.
        var verify = new VerifyEmailOtpCommandHandler(otpService, NewTokens());
        var stale = await verify.Handle(new VerifyEmailOtpCommand("a@b.com", first), default);
        Assert.False(stale.Success);
    }

    // --- send sms otp (needs a DbContext for the phone-exists check) ---------

    private static IdentityAppDbContext NewDb(params ApplicationUser[] seed)
    {
        var options = new DbContextOptionsBuilder<IdentityAppDbContext>()
            .UseInMemoryDatabase($"otp-slice-{Guid.NewGuid()}")
            .Options;
        var db = new IdentityAppDbContext(options);
        if (seed.Length > 0)
        {
            db.Users.AddRange(seed);
            db.SaveChanges();
        }
        return db;
    }

    [Fact]
    public async Task Send_sms_otp_does_not_reveal_that_a_phone_is_already_registered()
    {
        var sms = Substitute.For<ISmsNotificationService>();
        var existing = new ApplicationUser
        {
            Id = "u1", UserName = "639171234567", Email = "e@x.com",
            FullName = "Taken", Role = "Driver", PhoneNumber = "639171234567"
        };
        var handler = new SendSmsOtpCommandHandler(NewOtpService(), NewDb(existing), sms);

        var outcome = await handler.Handle(new SendSmsOtpCommand("0917 123 4567"), default);

        Assert.True(outcome.Success);
        Assert.Equal("If this phone number is registered, a verification code has been sent.", outcome.Message);
        // No SMS for a number that already exists.
        await sms.DidNotReceive().SendPhoneVerificationOtpAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Send_sms_otp_sends_to_a_new_number_using_the_normalized_form()
    {
        var sms = Substitute.For<ISmsNotificationService>();
        sms.SendPhoneVerificationOtpAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(true);
        var handler = new SendSmsOtpCommandHandler(NewOtpService(), NewDb(), sms);

        var outcome = await handler.Handle(new SendSmsOtpCommand("0917 123 4567"), default);

        Assert.True(outcome.Success);
        await sms.Received(1).SendPhoneVerificationOtpAsync(
            "639171234567", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Send_sms_otp_reports_success_even_when_the_provider_throws()
    {
        var sms = Substitute.For<ISmsNotificationService>();
        sms.SendPhoneVerificationOtpAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns<bool>(_ => throw new InvalidOperationException("gateway down"));
        var handler = new SendSmsOtpCommandHandler(NewOtpService(), NewDb(), sms);

        var outcome = await handler.Handle(new SendSmsOtpCommand("09171234567"), default);

        Assert.True(outcome.Success);
        Assert.Equal("If this phone number is valid, a verification code has been sent.", outcome.Message);
    }

    [Fact]
    public async Task Resend_sms_otp_reports_success_even_when_the_provider_returns_false()
    {
        var sms = Substitute.For<ISmsNotificationService>();
        sms.SendPhoneVerificationOtpAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(false);
        var handler = new ResendSmsOtpCommandHandler(NewOtpService(), sms);

        var outcome = await handler.Handle(new ResendSmsOtpCommand("09171234567"), default);

        // Anti-enumeration / no provider leakage.
        Assert.True(outcome.Success);
        Assert.Equal("If this phone number is valid, a new verification code has been sent.", outcome.Message);
    }
}
