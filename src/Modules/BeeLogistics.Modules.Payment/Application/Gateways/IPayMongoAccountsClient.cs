namespace BeeLogistics.Modules.Payment.Application.Gateways;

/// <summary>
/// PayMongo Platforms API: opening and operating the child accounts that hold driver balances.
/// <para>
/// Separate from <see cref="IPaymentGateway"/> on purpose. That interface is the provider-agnostic
/// contract every gateway implements; this one describes a capability only PayMongo has, so
/// forcing it into the shared abstraction would put methods on Xendit that can only throw.
/// </para>
/// </summary>
public interface IPayMongoAccountsClient
{
    /// <summary>
    /// Opens an empty consumer child account. The parent-child relationship forms automatically.
    /// <para>
    /// Takes no person data: PayMongo discards any sent at creation. Everything, including the
    /// email address, is supplied by <see cref="UpdateAccountAsync"/>.
    /// </para>
    /// </summary>
    /// <exception cref="PaymentGatewayException">
    /// 403 when the parent is not permitted to create child accounts.
    /// </exception>
    Task<PayMongoChildAccount> CreateConsumerAccountAsync(CancellationToken ct = default);

    /// <summary>
    /// Issues a hosted identity-verification session. The returned URL is handed to the driver and
    /// expires after roughly 72 hours.
    /// </summary>
    Task<PayMongoVerificationSession> StartIdentityVerificationAsync(string accountId, CancellationToken ct = default);

    /// <summary>
    /// Fills the person fields activation requires, including email and mobile. Only valid while
    /// the account is still pending — after activation PayMongo rejects this with 400 and the
    /// record is frozen.
    /// </summary>
    /// <exception cref="PaymentGatewayException">
    /// 409 when the email address is already registered anywhere on PayMongo.
    /// </exception>
    Task<PayMongoChildAccount> UpdateAccountAsync(string accountId, UpdateChildAccountRequest request, CancellationToken ct = default);

    /// <summary>
    /// Takes the account live and provisions its wallet.
    /// <para>
    /// Irreversible. It also makes the account read-only through the API, so every field must be
    /// correct first — including the name PayMongo's OCR extracted, which cannot be corrected
    /// afterwards. A decline is terminal for this account id.
    /// </para>
    /// </summary>
    Task<PayMongoChildAccount> ActivateAccountAsync(string accountId, CancellationToken ct = default);

    Task<PayMongoChildAccount?> GetAccountAsync(string accountId, CancellationToken ct = default);

    /// <summary>
    /// Reads the child's wallet, optionally including its account number and balance.
    /// </summary>
    /// <param name="includeAccount">Fetch account number, account name and ledger account id.</param>
    /// <param name="includeBalance">Fetch available and pending balance.</param>
    Task<PayMongoChildWallet?> GetWalletAsync(string accountId, bool includeAccount = false, bool includeBalance = false, CancellationToken ct = default);

    /// <summary>
    /// Moves money from the platform wallet into a driver's child wallet (earnings).
    /// </summary>
    /// <param name="referenceNumber">
    /// Caller-supplied idempotency handle. PayMongo rejects a reused one, which is what stops a
    /// redelivered booking event paying a driver twice.
    /// </param>
    Task<PayMongoInternalTransfer> TransferToChildAsync(
        string accountId,
        string destinationAccountNumber,
        string destinationAccountName,
        decimal amount,
        string referenceNumber,
        string description,
        CancellationToken ct = default);

    /// <summary>
    /// Moves money the other way — out of a driver's child wallet back to the platform wallet.
    /// <para>
    /// This is the rollback path for phase 2. Once earnings are landing in child wallets, undoing
    /// that means recovering balances that physically sit at PayMongo; without this, "turn the flag
    /// off" would strand every migrated driver's money where our ledger cannot reach it.
    /// </para>
    /// </summary>
    Task<PayMongoInternalTransfer> SweepFromChildAsync(
        string accountId,
        string sourceAccountNumber,
        string sourceAccountName,
        decimal amount,
        string referenceNumber,
        string description,
        CancellationToken ct = default);

    /// <summary>
    /// Sends money out of a driver's child wallet to their bank or e-wallet.
    /// <para>
    /// PayMongo takes its fee from the <b>source</b> wallet, so the child must hold
    /// <c>amount + fee</c> or the transfer is rejected with
    /// <c>source_account_balance: insufficient</c>. Callers must leave that headroom.
    /// </para>
    /// </summary>
    Task<PayMongoInternalTransfer> WithdrawFromChildAsync(
        ChildWalletPayoutRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Reads one of a child's transfers back, for reconciling a payout whose webhook never arrived.
    /// <para>
    /// Scoped with <c>Account-Id</c>: without it PayMongo resolves the id against the platform's
    /// own transfers, which would either miss it or return someone else's.
    /// </para>
    /// </summary>
    Task<PayMongoInternalTransfer?> GetChildTransferAsync(
        string accountId, string transferId, CancellationToken ct = default);

    /// <summary>
    /// Generates a QR Ph code that credits a PayMongo wallet.
    /// <para>
    /// Credits land <b>in real time over InstaPay</b> rather than waiting on the payment settlement
    /// run, which is the point: a checkout top-up does not reach the platform wallet until
    /// settlement, so until then the float is money we have credited but not received.
    /// </para>
    /// </summary>
    Task<PayMongoWalletQr> GenerateWalletQrAsync(
        GenerateWalletQrRequest request, CancellationToken ct = default);
}
