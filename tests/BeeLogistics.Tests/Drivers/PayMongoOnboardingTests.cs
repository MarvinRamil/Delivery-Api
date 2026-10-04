using BeeLogistics.Modules.Drivers.Application;
using BeeLogistics.Modules.Drivers.Application.Handlers;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Modules.Payment.Application.Gateways;
using Microsoft.AspNetCore.Identity;
using BeeLogistics.Modules.Drivers.Application.Interfaces;
using BeeLogistics.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace BeeLogistics.Tests.Drivers;

/// <summary>
/// Phase 1 onboarding (issue #91). The invariant under test throughout: <b>we never pay PayMongo
/// for a driver who has not already paid us</b>, and a driver is never charged for an account that
/// was not opened.
/// </summary>
public class PayMongoOnboardingTests
{
    private static readonly Guid DriverId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static DriverWalletOptions Opts(bool fees = true) => new()
    {
        PayMongoOnboardingEnabled = true,
        AccountFeesEnabled = fees,
        KycFeeAmount = 30m,
        PayMongoAccountEmailDomain = "bee.test"
    };

    private static (StartPayMongoOnboardingCommandHandler Handler,
                    FakeDriverWalletRepository Repo,
                    IPayMongoAccountsClient Accounts,
                    DriverWallet Wallet) Start(decimal topUpBalance, DriverWalletOptions? options = null)
    {
        var repo = new FakeDriverWalletRepository();
        var wallet = new DriverWallet(DriverId);
        if (topUpBalance > 0) wallet.AddTopUp(topUpBalance);
        repo.Wallets.Add(wallet);

        var accounts = Substitute.For<IPayMongoAccountsClient>();
        var handler = new StartPayMongoOnboardingCommandHandler(
            repo, accounts, Options.Create(options ?? Opts()),
            NullLogger<StartPayMongoOnboardingCommandHandler>.Instance);

        return (handler, repo, accounts, wallet);
    }

    private static PayMongoChildAccount Account(string id = "org_new") =>
        new(id, "consumer", PayMongoActivationStatus.Pending, null, null, false);

    private static PayMongoVerificationSession Session() =>
        new("verif_1", "https://identity.example/v/verif_1", "pending", null, null, DateTime.UtcNow.AddHours(72));

    // ── The sequencing invariant ───────────────────────────────────────

    [Fact]
    public async Task Onboarding_is_refused_when_the_float_cannot_cover_the_kyc_fee()
    {
        // The whole point of requiring the float first: PayMongo bills us for the KYC, so opening an
        // account for an unfunded driver is money we can never recover.
        var (handler, _, accounts, _) = Start(topUpBalance: 20m);

        var result = await handler.Handle(new StartPayMongoOnboardingCommand(DriverId), default);

        Assert.False(result.IsSuccess);
        Assert.Contains("30.00", result.Error);
        await accounts.DidNotReceive().CreateConsumerAccountAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Funded_driver_is_charged_exactly_once_and_linked()
    {
        var (handler, repo, accounts, wallet) = Start(topUpBalance: 1000m);
        accounts.CreateConsumerAccountAsync(Arg.Any<CancellationToken>())
            .Returns(Account());
        accounts.StartIdentityVerificationAsync("org_new", Arg.Any<CancellationToken>()).Returns(Session());

        var result = await handler.Handle(new StartPayMongoOnboardingCommand(DriverId), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(970m, wallet.TopUpBalance);
        Assert.Equal("org_new", wallet.PayMongoAccountId);

        var fee = Assert.Single(repo.Transactions, t => t.Type == WalletTransactionType.AccountFee);
        // Positive: the type carries the direction. Every other transaction type in the
        // codebase does the same, and a debit sum that subtracts by type would double-negate a
        // negative stored amount.
        Assert.Equal(30m, fee.Amount);
        Assert.Equal(WalletBucket.TopUp, fee.Bucket);
        // Keyed on the account id so a retry hits the unique index instead of charging again.
        Assert.Equal("acctfee-kyc-org_new", fee.ProviderPaymentId);
    }

    [Fact]
    public async Task Calling_start_again_reissues_verification_without_opening_a_second_account()
    {
        // A duplicate child account is permanent - PayMongo publishes no delete - and would split
        // the driver's balance across two wallets. It would also charge the fee twice.
        var (handler, repo, accounts, wallet) = Start(topUpBalance: 1000m);
        accounts.CreateConsumerAccountAsync(Arg.Any<CancellationToken>())
            .Returns(Account());
        accounts.StartIdentityVerificationAsync("org_new", Arg.Any<CancellationToken>()).Returns(Session());

        await handler.Handle(new StartPayMongoOnboardingCommand(DriverId), default);
        await handler.Handle(new StartPayMongoOnboardingCommand(DriverId), default);

        await accounts.Received(1).CreateConsumerAccountAsync(Arg.Any<CancellationToken>());
        await accounts.Received(2).StartIdentityVerificationAsync("org_new", Arg.Any<CancellationToken>());
        Assert.Single(repo.Transactions, t => t.Type == WalletTransactionType.AccountFee);
        Assert.Equal(970m, wallet.TopUpBalance);
    }

    [Fact]
    public async Task Fees_disabled_still_links_the_account_and_charges_nothing()
    {
        var (handler, repo, accounts, wallet) = Start(topUpBalance: 0m, options: Opts(fees: false));
        accounts.CreateConsumerAccountAsync(Arg.Any<CancellationToken>())
            .Returns(Account());
        accounts.StartIdentityVerificationAsync("org_new", Arg.Any<CancellationToken>()).Returns(Session());

        var result = await handler.Handle(new StartPayMongoOnboardingCommand(DriverId), default);

        Assert.True(result.IsSuccess);
        Assert.Equal("org_new", wallet.PayMongoAccountId);
        Assert.Empty(repo.Transactions);
        Assert.Equal(0m, wallet.TopUpBalance);
    }

    // ── Flags and guard rails ──────────────────────────────────────────

    [Fact]
    public async Task Disabled_flag_makes_the_whole_path_unreachable()
    {
        var (handler, _, accounts, _) = Start(topUpBalance: 1000m,
            options: new DriverWalletOptions { PayMongoOnboardingEnabled = false });

        var result = await handler.Handle(new StartPayMongoOnboardingCommand(DriverId), default);

        Assert.False(result.IsSuccess);
        await accounts.DidNotReceiveWithAnyArgs().CreateConsumerAccountAsync(default);
    }

    [Fact]
    public async Task Missing_email_domain_refuses_rather_than_sending_a_placeholder()
    {
        // The address is frozen onto the account at creation and cannot be corrected afterwards,
        // so a wrong one is permanent.
        var opts = Opts();
        opts.PayMongoAccountEmailDomain = "";
        var (handler, _, accounts, _) = Start(topUpBalance: 1000m, options: opts);

        var result = await handler.Handle(new StartPayMongoOnboardingCommand(DriverId), default);

        Assert.False(result.IsSuccess);
        await accounts.DidNotReceiveWithAnyArgs().CreateConsumerAccountAsync(default);
    }

    [Fact]
    public async Task Declined_driver_is_not_retried()
    {
        // PayMongo's risk review cannot be appealed and the account id cannot be reused, so a retry
        // can only fail - and would charge the fee again if it did not stop here.
        var (handler, _, accounts, wallet) = Start(topUpBalance: 1000m);
        wallet.LinkPayMongoAccount("org_dead");
        wallet.SetPayMongoActivationStatus(PayMongoActivationStatus.Declined);

        var result = await handler.Handle(new StartPayMongoOnboardingCommand(DriverId), default);

        Assert.False(result.IsSuccess);
        Assert.Contains("declined", result.Error, StringComparison.OrdinalIgnoreCase);
        await accounts.DidNotReceiveWithAnyArgs().CreateConsumerAccountAsync(default);
    }

    [Fact]
    public async Task Failed_creation_does_not_leave_the_driver_charged_for_nothing()
    {
        // If creation is rejected there is no account, so there must be no fee either. The fee and
        // the account link commit together for exactly this reason.
        var (handler, repo, accounts, wallet) = Start(topUpBalance: 1000m);
        accounts.CreateConsumerAccountAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new PaymentGatewayException("PayMongo", 503, "unavailable", "upstream down"));

        var result = await handler.Handle(new StartPayMongoOnboardingCommand(DriverId), default);

        Assert.False(result.IsSuccess);
        Assert.Equal(1000m, wallet.TopUpBalance);
        Assert.Empty(repo.Transactions);
        Assert.Null(wallet.PayMongoAccountId);
    }

    // ── Domain invariants ──────────────────────────────────────────────

    [Fact]
    public void Relinking_a_wallet_to_a_different_account_is_refused()
    {
        // Re-linking would strand whatever balance sits in the previously linked wallet, which the
        // aggregate can no longer see or reach.
        var wallet = new DriverWallet(DriverId);
        wallet.LinkPayMongoAccount("org_a");

        Assert.Throws<InvalidOperationException>(() => wallet.LinkPayMongoAccount("org_b"));
        wallet.LinkPayMongoAccount("org_a"); // same id stays idempotent
        Assert.Equal("org_a", wallet.PayMongoAccountId);
    }

    [Fact]
    public void Declined_is_terminal()
    {
        var wallet = new DriverWallet(DriverId);
        wallet.LinkPayMongoAccount("org_a");
        wallet.SetPayMongoActivationStatus(PayMongoActivationStatus.Declined);

        Assert.Throws<InvalidOperationException>(
            () => wallet.SetPayMongoActivationStatus(PayMongoActivationStatus.Activated));
    }

    [Fact]
    public void Wallet_is_only_usable_once_it_has_an_addressable_account_number()
    {
        // UsesPayMongoWallet gates earnings routing. Without the account number there is no
        // destination_account.number to pay into, so "activated" alone is not enough.
        var wallet = new DriverWallet(DriverId);
        wallet.LinkPayMongoAccount("org_a");
        Assert.False(wallet.UsesPayMongoWallet);

        wallet.SetPayMongoWallet("wallet_a", accountNumber: null, ledgerAccountId: null);
        Assert.False(wallet.UsesPayMongoWallet);

        wallet.SetPayMongoWallet("wallet_a", "285168654745", "ledger_1");
        Assert.True(wallet.UsesPayMongoWallet);
    }

    [Fact]
    public void Account_fee_cannot_push_the_float_past_its_floor()
    {
        var wallet = new DriverWallet(DriverId);
        wallet.AddTopUp(20m);

        // Opening fee passes 0: it must fail rather than lend the driver money.
        Assert.Throws<InvalidOperationException>(() => wallet.ChargeAccountFee(30m, allowedNegativeLimit: 0m));
        Assert.Equal(20m, wallet.TopUpBalance);

        // Monthly upkeep is allowed to accrue against the float down to the configured floor.
        wallet.ChargeAccountFee(30m, allowedNegativeLimit: -500m);
        Assert.Equal(-10m, wallet.TopUpBalance);
    }

    [Fact]
    public void Attaching_a_wallet_before_linking_an_account_is_refused()
    {
        var wallet = new DriverWallet(DriverId);
        Assert.Throws<InvalidOperationException>(
            () => wallet.SetPayMongoWallet("wallet_a", "285168654745", "ledger_1"));
    }
}

/// <summary>
/// Concurrency on the onboarding path. Creating a child account is irreversible — PayMongo
/// publishes no delete and may bill us for the KYC — so two concurrent requests must not both
/// reach it.
/// </summary>
public class PayMongoOnboardingConcurrencyTests
{
    private static readonly Guid DriverId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    [Fact]
    public async Task A_concurrent_second_request_does_not_create_a_second_account()
    {
        // The loser of the xmin race must fail BEFORE anything exists at PayMongo. Without the
        // claim-first ordering both requests read PayMongoAccountId as null, both call PayMongo,
        // and the account that loses the save is orphaned forever.
        var wallet = new DriverWallet(DriverId);
        wallet.AddTopUp(1000m);

        var repo = Substitute.For<IDriverWalletRepository>();
        repo.GetWalletByDriverIdAsync(DriverId, Arg.Any<CancellationToken>()).Returns(wallet);
        // The other request already moved this row, so our save loses on xmin.
        repo.UpdateWalletAsync(Arg.Any<DriverWallet>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DbUpdateConcurrencyException());

        var accounts = Substitute.For<IPayMongoAccountsClient>();
        var handler = new StartPayMongoOnboardingCommandHandler(
            repo, accounts,
            Options.Create(new DriverWalletOptions
            {
                PayMongoOnboardingEnabled = true,
                AccountFeesEnabled = true,
                KycFeeAmount = 30m,
                PayMongoAccountEmailDomain = "bee.test"
            }),
            NullLogger<StartPayMongoOnboardingCommandHandler>.Instance);

        var result = await handler.Handle(new StartPayMongoOnboardingCommand(DriverId), default);

        Assert.False(result.IsSuccess);
        Assert.Contains("already in progress", result.Error);
        await accounts.DidNotReceiveWithAnyArgs().CreateConsumerAccountAsync(default);
    }
}

/// <summary>
/// Which address a child account is opened under.
///
/// <para>PayMongo requires it to be unique across <b>every</b> account on its platform and freezes
/// it at activation, so a driver who already has one cannot use their plain address and there is no
/// correcting it later. The ladder prefers addresses that still reach their real inbox: their own,
/// then a plus-tagged variant of it (distinct to PayMongo, same mailbox at Gmail and most other
/// providers), and only then a platform alias they would never read.</para>
/// </summary>
public class PayMongoOnboardingEmailTests
{
    private static readonly Guid DriverId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static string Token =>
        SubmitPayMongoOnboardingDetailsCommandHandler.ShortDriverToken(DriverId);
    private static string PlatformAlias => $"driver+{Token}@bee.test";

    private static List<string> Candidates(string? driverEmail, string domain = "bee.test")
        => SubmitPayMongoOnboardingDetailsCommandHandler.BuildEmailCandidates(driverEmail, DriverId, domain);

    [Fact]
    public void Their_own_address_is_tried_first()
    {
        var candidates = Candidates("juan@example.ph");
        Assert.Equal("juan@example.ph", candidates[0]);
    }

    [Fact]
    public void The_second_try_is_a_plus_tag_on_their_own_domain_so_it_still_reaches_them()
    {
        // The point of this rung: PayMongo sees a distinct address, but Gmail and most providers
        // deliver it to the same inbox - so the driver still receives PayMongo's notices and can
        // still reset their password. A platform alias cannot do that.
        var candidates = Candidates("juan@example.ph");
        Assert.Equal($"juan+bee{Token}@example.ph", candidates[1]);
    }

    [Fact]
    public void The_platform_alias_is_last()
    {
        var candidates = Candidates("juan@example.ph");
        Assert.Equal(PlatformAlias, candidates[^1]);
        Assert.Equal(3, candidates.Count);
    }

    [Fact]
    public void An_existing_tag_is_replaced_rather_than_stacked()
    {
        // "juan+shop+beeXXXX@" is not parsed consistently across providers, and could bounce.
        var candidates = Candidates("juan+shop@example.ph");
        Assert.Equal($"juan+bee{Token}@example.ph", candidates[1]);
        Assert.DoesNotContain(candidates, c => c.Contains("+shop+"));
    }

    [Fact]
    public void A_driver_with_no_email_on_file_gets_the_alias_only()
    {
        var candidates = Candidates(null);
        Assert.Equal(new[] { PlatformAlias }, candidates);
    }

    [Fact]
    public void No_email_and_no_configured_domain_yields_nothing_to_try()
    {
        // Onboarding refuses rather than inventing an address: it is frozen at activation.
        var candidates = Candidates(null, domain: "");
        Assert.Empty(candidates);
    }

    [Fact]
    public void A_malformed_address_still_falls_back_to_the_alias()
    {
        var candidates = Candidates("not-an-email");
        Assert.Equal("not-an-email", candidates[0]);
        Assert.Equal(PlatformAlias, candidates[^1]);
    }

    [Fact]
    public void The_token_is_short_and_stable()
    {
        // Stable: a retry must produce the SAME address, or it would open a second account.
        Assert.Equal(Token, SubmitPayMongoOnboardingDetailsCommandHandler.ShortDriverToken(DriverId));
        Assert.Equal(6, Token.Length);
        Assert.All(Token, c => Assert.True(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)));
    }

    [Fact]
    public void Different_drivers_get_different_tokens()
    {
        var a = SubmitPayMongoOnboardingDetailsCommandHandler.ShortDriverToken(Guid.NewGuid());
        var b = SubmitPayMongoOnboardingDetailsCommandHandler.ShortDriverToken(Guid.NewGuid());
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void The_alias_is_short_enough_to_read_aloud()
    {
        // It ends up in the driver's own inbox and in support conversations. The full 32-character
        // Guid it replaced was not something anyone could read back over the phone.
        Assert.Equal("driver+" + Token + "@bee.test", PlatformAlias);
        Assert.True(PlatformAlias.Length <= 32, PlatformAlias);
    }
}

/// <summary>
/// Identity-verification failures.
///
/// <para>PayMongo reports these as <c>status: "completed"</c> with <c>result: "failed"</c> — the
/// session finished, the check did not pass. The reason is usually something the driver can fix in
/// one retry ("Image quality check failed: blur detection"), so it has to reach them; a stuck
/// screen with no explanation is the worst outcome.</para>
/// </summary>
public class PayMongoVerificationFailureTests
{
    private static readonly Guid DriverId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private const string Reason = "Data is not verified; Image quality check failed: blur detection";

    private static (SyncPayMongoAccountStatusCommandHandler Handler, DriverWallet Wallet, FakeDriverWalletRepository Repo)
        Setup(PayMongoActivationStatus status = PayMongoActivationStatus.Verifying)
    {
        var repo = new FakeDriverWalletRepository();
        var wallet = new DriverWallet(DriverId);
        wallet.LinkPayMongoAccount("org_a");
        wallet.SetPayMongoActivationStatus(status);
        repo.Wallets.Add(wallet);

        var accounts = Substitute.For<IPayMongoAccountsClient>();
        return (new SyncPayMongoAccountStatusCommandHandler(
            repo, accounts, NullLogger<SyncPayMongoAccountStatusCommandHandler>.Instance), wallet, repo);
    }

    [Fact]
    public async Task A_failed_check_stores_the_reason_and_stays_retryable()
    {
        // Pending, not a terminal failure: the driver can be issued a fresh session and usually
        // passes on the second try.
        var (handler, wallet, _) = Setup();

        await handler.Handle(new SyncPayMongoAccountStatusCommand(
            "org_a", "account.identity_verification.failed", null, Reason), default);

        Assert.Equal(PayMongoActivationStatus.Pending, wallet.PayMongoActivationStatus);
        Assert.Equal(Reason, wallet.PayMongoVerificationFailureReason);
    }

    [Fact]
    public async Task A_later_pass_clears_the_earlier_reason()
    {
        // Otherwise the driver keeps reading why a previous attempt failed after the retry has
        // already succeeded.
        var (handler, wallet, _) = Setup();
        await handler.Handle(new SyncPayMongoAccountStatusCommand(
            "org_a", "account.identity_verification.failed", null, Reason), default);

        await handler.Handle(new SyncPayMongoAccountStatusCommand(
            "org_a", "account.identity_verification.passed", null), default);

        Assert.Equal(PayMongoActivationStatus.Verified, wallet.PayMongoActivationStatus);
        Assert.Null(wallet.PayMongoVerificationFailureReason);
    }

    [Fact]
    public void A_completed_session_that_did_not_pass_is_a_failure()
    {
        // The trap: status is the session LIFECYCLE, result is the outcome. Reading status alone
        // makes "completed / failed" look like success.
        var failed = new PayMongoVerificationSession(
            "verif_1", "https://x", Status: "completed", Result: "failed", FailureReason: Reason, null);

        Assert.True(failed.Failed);
        Assert.False(failed.Passed);
        Assert.Equal(Reason, failed.FailureReason);
    }

    [Fact]
    public void A_completed_session_that_passed_is_a_pass()
    {
        var passed = new PayMongoVerificationSession(
            "verif_1", "https://x", Status: "completed", Result: "passed", FailureReason: null, null);

        Assert.True(passed.Passed);
        Assert.False(passed.Failed);
    }
}

/// <summary>
/// Fields activation rejects, caught before the irreversible call (issue #91).
///
/// <para>Each of these was a real failure on the first live run. They matter more than an ordinary
/// validation error because ACTIVATE is irreversible and freezes the account: reaching it with bad
/// data means the child account is already created and its KYC already paid for.</para>
/// </summary>
public class PayMongoActivationValidationTests
{
    [Fact]
    public void Every_province_PayMongo_names_in_its_error_is_accepted()
    {
        // The 400 read: "state must be a valid Philippine province code (e.g., PH-MNL, PH-CAV,
        // PH-CEB)". All three must pass, or our list disagrees with theirs.
        Assert.True(PhProvinces.IsValid("PH-MNL"));
        Assert.True(PhProvinces.IsValid("PH-CAV"));
        Assert.True(PhProvinces.IsValid("PH-CEB"));
    }

    [Fact]
    public void The_province_that_was_rejected_live_is_itself_valid()
    {
        // PH-ILN is on PayMongo's list, so the live rejection was a typed value that was not a
        // code at all — which is why the app must offer a picker, not a text field.
        Assert.True(PhProvinces.IsValid("PH-ILN"));
        Assert.Equal("Ilocos Norte", PhProvinces.NameFor("PH-ILN"));
    }

    [Theory]
    [InlineData("Ilocos Norte")]   // the province name, not its code
    [InlineData("ILN")]            // missing the PH- prefix
    [InlineData("PH-XXX")]         // well-formed but not a real code
    [InlineData("")]
    [InlineData(null)]
    public void Anything_that_is_not_a_code_is_rejected(string? value)
        => Assert.False(PhProvinces.IsValid(value));

    [Fact]
    public void Codes_are_matched_case_insensitively()
        => Assert.True(PhProvinces.IsValid("ph-iln"));

    [Fact]
    public void The_list_is_complete_and_free_of_duplicates()
    {
        // 82 per PayMongo's published list. A duplicate would mean a copy/paste slip that silently
        // drops a province, and a driver in it could never finish setup.
        Assert.Equal(82, PhProvinces.All.Count);
        Assert.Equal(82, PhProvinces.All.Select(p => p.Code).Distinct().Count());
        Assert.All(PhProvinces.All, p => Assert.StartsWith("PH-", p.Code));
    }
}
