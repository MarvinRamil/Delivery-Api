namespace BeeLogistics.Modules.Payment.Application.Gateways;

/// <summary>
/// Models for PayMongo's Platforms / Onboarding-as-a-Service API: child ("linked") accounts,
/// each of which gets its own wallet on activation.
///
/// Shapes here were verified against the live API on 2026-08-30, not taken from the docs,
/// which are wrong in at least one place — see <see cref="PayMongoVerificationSession.Url"/>.
/// </summary>
public static class PayMongoAccountTypes
{
    /// <summary>Individual. KYC of the person only, no business documents. Gets a wallet.</summary>
    public const string Consumer = "consumer";

    /// <summary>Business. KYC + KYB. Gets a wallet plus QR Ph (P2M) acceptance. Not used for drivers.</summary>
    public const string Merchant = "merchant";
}

/// <summary>
/// Where a child account sits in the create -> verify -> activate lifecycle.
/// <para>
/// <c>Declined</c> is terminal <b>for that account id</b>: PayMongo's risk review cannot be
/// appealed and the account cannot be reused. Recovering means creating a new child.
/// </para>
/// </summary>
public enum PayMongoActivationStatus
{
    /// <summary>No child account exists for this driver yet.</summary>
    None = 0,

    /// <summary>Account created; identity verification not yet passed.</summary>
    Pending = 1,

    /// <summary>Hosted verification session issued, awaiting the driver.</summary>
    Verifying = 2,

    /// <summary>Identity verification passed; not yet activated.</summary>
    Verified = 3,

    /// <summary>Live. A wallet exists and can send and receive.</summary>
    Activated = 4,

    /// <summary>Rejected by risk review. Terminal for this account id.</summary>
    Declined = 5
}

/// <summary>
/// The remaining person fields activation requires.
///
/// <para><b>Verified against live activation on 2026-08-30.</b> Calling activate on an empty child
/// returns the exact required set, which is: first_name, last_name, mobile_number, email_address,
/// date_of_birth (day/month/year), nationality, nature_of_work, place_of_birth (city/country),
/// source_of_funds, and address (line1/city/state/postal_code/country).</para>
///
/// <para>Name and date of birth are filled by identity verification from the ID, so they are not
/// on this record. <b>TIN is NOT required</b> despite the activation guide listing it as a
/// prerequisite — it is genuinely optional, which matters because many riders do not have one.</para>
///
/// <para>Email and mobile are here rather than on creation because
/// <c>POST /v2/accounts</c> <b>silently discards</b> any person object sent with it: fields passed
/// there come back null and activation then fails demanding them. PayMongo's quick start shows them
/// in the create body regardless.</para>
/// </summary>

public sealed record UpdateChildAccountRequest(
    // Must be unique across ALL of PayMongo - a driver who already has an account cannot reuse
    // their own address, and the patch returns 409. Plus-addressing is accepted, so callers pass a
    // platform-controlled alias.
    string EmailAddress,
    string MobileNumber,
    string? MiddleName,
    string Nationality,
    string NatureOfWork,
    string SourceOfFunds,
    // Optional: proven not to be in activation's required set, despite the guide saying otherwise.
    string? Tin,
    string PlaceOfBirthCity,
    string PlaceOfBirthCountry,
    string AddressLine1,
    string AddressCity,
    // ISO 3166-2 subdivision, e.g. "PH-ILN". Not the province name.
    string AddressState,
    string AddressCountry,
    string AddressPostalCode,
    string? SourceOfFundsOther = null);

/// <summary>A child account as PayMongo reports it.</summary>
public sealed record PayMongoChildAccount(
    string AccountId,
    string Type,
    PayMongoActivationStatus ActivationStatus,
    string? FirstName,
    string? LastName,
    bool IdentityVerificationPassed);

/// <summary>
/// A hosted identity-verification session. PayMongo fronts PowerCred for this.
/// </summary>
public sealed record PayMongoVerificationSession(
    string VerificationId,
    // The link to hand the driver. PayMongo's quick start documents this field as
    // "hosted_url"; the live API returns "url". Reading the documented name yields null
    // and a broken link, so this is deliberately mapped from "url".
    string Url,
    // Session LIFECYCLE - "pending" while the driver has not finished, "completed" once they have.
    // Not the outcome: a session can be "completed" with a "failed" result.
    string Status,
    // The OUTCOME - "passed" or "failed". This is the field that decides anything.
    string? Result,
    // Why it failed, in terms a driver can act on, e.g.
    // "Data is not verified; Image quality check failed: blur detection".
    string? FailureReason,
    DateTime? ExpiresAt)
{
    public bool Passed => string.Equals(Result, "passed", StringComparison.OrdinalIgnoreCase);
    public bool Failed => string.Equals(Result, "failed", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A child's wallet. Balance and account number are fetched separately because PayMongo's
/// <c>fields</c> query parameter accepts only one value per request — see
/// <c>PayMongoAccountsClient.GetWalletAsync</c>.
/// </summary>
public sealed record PayMongoChildWallet(
    string WalletId,
    string MerchantId,
    string Status,
    // "default" can send externally; "closed_loop" cannot.
    string Type,
    string? AccountNumber,
    string? AccountName,
    string? LedgerAccountId,
    decimal? AvailableBalance,
    decimal? PendingBalance);

/// <summary>
/// An in-network wallet-to-wallet transfer between the platform and one of its child accounts.
/// <para>
/// These use <c>provider: "paymongo"</c> rather than instapay/pesonet, and cost nothing — verified
/// live: a ₱40 parent→child transfer settled with <c>fee: 0</c>. <see cref="Fee"/> is still read
/// back rather than assumed, because the fee PayMongo actually charges has varied by channel.
/// </para>
/// </summary>
public sealed record PayMongoInternalTransfer(
    string TransferId,
    string Status,
    decimal Amount,
    decimal Fee,
    string? ProviderErrorMessage)
{
    public bool Succeeded => Status.Equals("succeeded", StringComparison.OrdinalIgnoreCase);
    public bool Failed => Status.Equals("failed", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// An outbound transfer leaving a driver's child wallet for their own bank or e-wallet.
/// <para>
/// Distinct from <c>CreateGatewayDisbursementRequest</c>, which is the provider-agnostic payout
/// contract every gateway implements. This one carries things only PayMongo has — the child account
/// to act as, and that child's wallet as the source — so folding it into the shared contract would
/// put fields on Xendit that can never mean anything.
/// </para>
/// </summary>
public sealed record ChildWalletPayoutRequest(
    string AccountId,
    string SourceAccountNumber,
    string SourceAccountName,
    string DestinationAccountNumber,
    string DestinationAccountName,
    // Per-rail: the same institution has different BICs for InstaPay and PESONet.
    string DestinationBic,
    // "instapay" or "pesonet".
    string Provider,
    decimal Amount,
    string ReferenceNumber,
    string Description);

/// <summary>
/// Which QR Ph scheme a wallet QR uses. PayMongo enforces the distinction: a consumer (child)
/// wallet can only receive P2P, a merchant account only P2M. Sending the wrong one fails
/// <c>Field 'Mode' validation failed: oneof</c>.
/// </summary>
public enum WalletQrMode
{
    /// <summary>Person-to-person — a driver's own child wallet.</summary>
    P2P,

    /// <summary>Person-to-merchant — the platform account.</summary>
    P2M
}

/// <summary>
/// Static QRs carry no amount and never expire; dynamic QRs encode an amount and expire.
/// </summary>
public enum WalletQrType
{
    /// <summary>Reusable. The scanner types the amount. One per driver, generated once.</summary>
    Static,

    /// <summary>Amount fixed at generation, expires (60–9000s, default 1800).</summary>
    Dynamic
}

/// <param name="OnBehalfOfAccountId">
/// Child account to generate for, via <c>Account-Id</c>. Null generates for the platform.
/// This is what makes the split possible: the driver's QR shows their own name and credits their
/// own wallet, rather than everything landing on the platform merchant.
/// </param>
/// <param name="Amount">Required for <see cref="WalletQrType.Dynamic"/>, ignored for static.</param>
/// <param name="ReferenceLabel">
/// Carried into the QR string and echoed back on the payment, so a top-up can be matched to the
/// record that requested it. Meaningless on a static QR, which many people may scan.
/// </param>
public sealed record GenerateWalletQrRequest(
    string? OnBehalfOfAccountId,
    WalletQrMode Mode,
    WalletQrType Type,
    decimal? Amount = null,
    string? ReferenceLabel = null);

/// <summary>
/// A QR Ph code that credits a PayMongo wallet.
///
/// <para>
/// Unlike a checkout session, this is <b>not</b> a payment awaiting settlement — QR Ph settles in
/// real time over InstaPay, so the wallet is credited immediately. That is the whole reason this
/// exists: checkout top-ups only reach the platform wallet on the weekly settlement run, which
/// means fronting every driver's float until it lands.
/// </para>
/// </summary>
public sealed record PayMongoWalletQr(
    string QrId,
    // The EMV payload to render. This is what a GCash or bank app scans.
    string QrString,
    string Mode,
    string Type,
    string Status,
    string? MerchantName,
    // The wallet being credited. Present on P2P; absent on P2M, where the merchant id
    // identifies it instead.
    string? CreditAccountNumber,
    string? MerchantId,
    decimal? Amount,
    string? ReferenceLabel,
    DateTime? ExpiresAt);
