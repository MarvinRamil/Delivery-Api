using System.Text.RegularExpressions;

namespace BeeLogistics.Modules.Messaging.Application;

/// <summary>
/// Configuration for the Matrix homeserver bee-backend talks to as an Application Service.
///
/// The backend is not a Matrix <em>client</em>: it holds the appservice tokens and acts on behalf
/// of namespaced users via <c>?user_id=</c> masquerading. That is why there is no SDK here and no
/// per-user credential — <see cref="AsToken"/> is the only outbound secret, and
/// <see cref="HsToken"/> is the only thing that authenticates Synapse when it calls us back.
/// </summary>
public partial class MatrixOptions
{
    public const string SectionName = "Matrix";

    /// <summary>
    /// Master switch. While false, nothing provisions rooms, no appservice route accepts traffic,
    /// and booking creation behaves exactly as it did before this module existed.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Where the backend reaches Synapse — a LAN address, since Synapse runs on its own VM rather
    /// than as a container beside the API. Plain HTTP by design: this hop never leaves the private
    /// network, and the URL never leaves the backend.
    /// </summary>
    /// <remarks>
    /// The default is the local docker-compose service name and is useful only for local
    /// development. Deployed environments must set this explicitly; CI deliberately passes it with
    /// no fallback, because a container name that resolves nowhere would still satisfy
    /// <see cref="Validate"/> and turn a missing variable into "no booking ever gets a chat room".
    /// </remarks>
    public string HomeserverUrl { get; set; } = "http://beeapp-synapse:8008";

    /// <summary>
    /// The public HTTPS URL handed to client apps in their session response. Distinct from
    /// <see cref="HomeserverUrl"/> because clients cannot resolve a container name. Falls back to
    /// <see cref="HomeserverUrl"/> when blank, which is what makes local dev work unconfigured.
    /// </summary>
    public string PublicHomeserverUrl { get; set; } = string.Empty;

    /// <summary>
    /// The MXID domain — the part after the colon in <c>@bee_u_prod_…:matrix.bee-app.tech</c>. This
    /// is Synapse's <c>server_name</c> and is <b>permanent</b>: changing it invalidates every user
    /// and room ever created. Identical in every environment, because there is one homeserver.
    /// </summary>
    /// <remarks>
    /// It is the homeserver's own hostname, so no <c>/.well-known/matrix/client</c> delegation is
    /// needed — clients resolve the server directly from the MXID.
    /// </remarks>
    public string ServerName { get; set; } = string.Empty;

    /// <summary>
    /// Which environment this backend is. Prefixes every Matrix user and room alias it creates:
    /// <c>@bee_u_{prefix}_…</c> and <c>#booking-{prefix}-…</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is an isolation boundary, not a label.</b> Every environment shares one homeserver
    /// but has its own database, and <c>BookingNumber</c> is only unique
    /// <em>within</em> a database (unique index at <c>BookingsDbContext.cs:92</c>). Two
    /// environments can therefore mint the same booking number on the same day. Without this
    /// prefix their room aliases collide, and because room creation adopts an existing alias
    /// rather than failing, a dev tester would land inside a real customer's booking chat.
    /// </para>
    /// <para>
    /// Each environment registers its own application service whose namespaces are declared
    /// <c>exclusive</c>, so Synapse itself refuses to let one environment create or claim another's
    /// users and aliases. The prefix is what makes that server-side guarantee possible; it is not
    /// relied on for correctness on its own.
    /// </para>
    /// </remarks>
    public string EnvironmentPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Must match <c>id</c> in the registration file Synapse loads, and is per-environment
    /// (<c>bee-appservice-dev</c>, <c>bee-appservice-prod</c>, …) since one homeserver loads all of them.
    /// </summary>
    public string AppServiceId { get; set; } = string.Empty;

    /// <summary>
    /// Localpart of the bot that owns every booking room and posts status updates. Must match
    /// <c>sender_localpart</c> in this environment's registration file, and must be unique across
    /// the registration files on the shared homeserver — <c>bee</c> for production, <c>bee-dev</c>
    /// for dev.
    /// </summary>
    public string SenderLocalpart { get; set; } = string.Empty;

    /// <summary>Bearer token the backend sends to Synapse. Vault-sourced; never logged.</summary>
    public string AsToken { get; set; } = string.Empty;

    /// <summary>
    /// Bearer token Synapse sends to us on <c>PUT /_matrix/app/v1/transactions/{txnId}</c>.
    /// Compared in constant time — see MatrixTransactionAuthenticator.
    /// </summary>
    public string HsToken { get; set; } = string.Empty;

    /// <summary>
    /// Synapse Admin API token, used only by the retention job to purge expired rooms. Separate
    /// from <see cref="AsToken"/> because the appservice has no admin rights and should not.
    /// </summary>
    public string AdminToken { get; set; } = string.Empty;

    /// <summary>
    /// Where the backend reaches Synapse's <b>admin</b> API. Falls back to
    /// <see cref="HomeserverUrl"/> when blank, so an environment that does not set it behaves
    /// exactly as before.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because the admin API is blocked on the public hostname — <c>/_synapse/*</c>
    /// returns 404 from the reverse proxy — while the client-server and appservice APIs on
    /// <c>/_matrix/*</c> are not. Purging a room is the only thing that needs it.
    /// </para>
    /// <para>
    /// Setting this to a LAN address does <b>not</b> undo the deliberate choice to route ordinary
    /// traffic over the public path (see <c>docker/synapse/README.md</c>). That choice is about
    /// exercising what clients and Synapse itself will use; the admin API is backend-only, never
    /// client-facing, and blocking it at the edge is a security property worth keeping rather than
    /// punching an allowlist hole through.
    /// </para>
    /// </remarks>
    public string AdminApiUrl { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>
    /// How long after a booking reaches Completed/Cancelled before its room becomes read-only.
    /// Generous on purpose: a delivery dispute usually surfaces after the trip ends, and freezing
    /// mid-conversation is worse than leaving a finished room open a while longer.
    /// </summary>
    public int RoomFreezeDelayHours { get; set; } = 24;

    /// <summary>
    /// How long a frozen room survives on Synapse before it is purged. The Postgres archive is
    /// unaffected — purging reclaims homeserver storage, it does not destroy the transcript.
    /// </summary>
    public int RoomRetentionDays { get; set; } = 90;

    // --- Computed. NOT configuration keys. ---
    // The options binder silently ignores get-only properties, so adding e.g. a "Matrix:BotUserId"
    // key would bind to nothing and look like it worked. Everything below is derived; if you need
    // to change it, change the setter it derives from.

    /// <summary>The bot's full MXID, e.g. <c>@bee:matrix.bee-app.tech</c>. Computed — not bindable.</summary>
    public string BotUserId => $"@{SenderLocalpart}:{ServerName}";

    /// <summary>
    /// Localpart prefix for every user this environment creates: <c>bee_u_prod_</c>. Computed.
    /// </summary>
    public string UserLocalpartPrefix => $"bee_u_{EnvironmentPrefix}_";

    /// <summary>
    /// Localpart prefix for every room alias this environment creates: <c>booking-prod-</c>. Computed.
    /// </summary>
    public string RoomAliasPrefix => $"booking-{EnvironmentPrefix}-";

    /// <summary>
    /// The MXID this environment uses for a bee Identity user. One Matrix account per bee identity,
    /// so someone who is both a customer and a driver has a single account rather than two.
    /// </summary>
    public string MatrixUserIdFor(string identityUserId) =>
        $"@{UserLocalpartPrefix}{identityUserId.ToLowerInvariant()}:{ServerName}";

    /// <summary>
    /// The room alias for a booking. Doubles as the idempotency key for room creation: creating an
    /// alias that already exists returns <c>M_ROOM_IN_USE</c>, which is the signal to adopt the
    /// existing room instead of making a second one.
    /// </summary>
    /// <remarks>
    /// Lowercased because Matrix aliases are case-sensitive while <c>BookingNumber</c> is uppercase
    /// — without this, a lookup that differed only in case would miss the existing room and create
    /// a duplicate. The environment prefix is what keeps dev's <c>BKG-20260823-000123</c> from
    /// resolving to production's room of the same number; see <see cref="EnvironmentPrefix"/>.
    /// </remarks>
    public string RoomAliasFor(string bookingNumber) =>
        $"#{RoomAliasPrefix}{bookingNumber.ToLowerInvariant()}:{ServerName}";

    /// <summary>What clients are told to connect to. Computed — not bindable.</summary>
    public string ClientFacingHomeserverUrl =>
        string.IsNullOrWhiteSpace(PublicHomeserverUrl) ? HomeserverUrl : PublicHomeserverUrl;

    /// <summary>
    /// Where admin-API calls actually go: <see cref="AdminApiUrl"/> when set, otherwise
    /// <see cref="HomeserverUrl"/>. Computed — not bindable.
    /// </summary>
    public string AdminApiBaseUrl =>
        string.IsNullOrWhiteSpace(AdminApiUrl) ? HomeserverUrl : AdminApiUrl;

    /// <summary>
    /// Configuration problems that make the module unusable, as human-readable strings.
    /// Empty when <see cref="Enabled"/> is false — a disabled integration needs no secrets, which
    /// is what lets CI and local dev run without any Matrix configuration at all.
    /// </summary>
    /// <summary>
    /// True only for an absolute http/https URL.
    /// </summary>
    /// <remarks>
    /// The scheme check is the whole point. <c>Uri.TryCreate(…, UriKind.Absolute, …)</c> happily
    /// accepts <c>"beeapp-synapse:8008"</c> — it reads as scheme <c>beeapp-synapse</c> with path
    /// <c>8008</c> — so an absoluteness check alone lets a host:port typo through, and HttpClient
    /// then throws at first use rather than at startup.
    /// </remarks>
    public static bool IsUsableHomeserverUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (!Enabled)
            return problems;

        if (string.IsNullOrWhiteSpace(HomeserverUrl))
            problems.Add($"{SectionName}:HomeserverUrl is required when {SectionName}:Enabled is true");
        else if (!IsUsableHomeserverUrl(HomeserverUrl))
            problems.Add($"{SectionName}:HomeserverUrl must be an absolute http(s) URL (got '{HomeserverUrl}')");

        if (!string.IsNullOrWhiteSpace(PublicHomeserverUrl) &&
            !IsUsableHomeserverUrl(PublicHomeserverUrl))
            problems.Add($"{SectionName}:PublicHomeserverUrl must be an absolute http(s) URL (got '{PublicHomeserverUrl}')");

        if (!string.IsNullOrWhiteSpace(AdminApiUrl) &&
            !IsUsableHomeserverUrl(AdminApiUrl))
            problems.Add($"{SectionName}:AdminApiUrl must be an absolute http(s) URL (got '{AdminApiUrl}')");

        if (string.IsNullOrWhiteSpace(ServerName))
            problems.Add($"{SectionName}:ServerName is required when {SectionName}:Enabled is true");
        else if (ServerName.Contains("://", StringComparison.Ordinal) || ServerName.Contains('/'))
            problems.Add($"{SectionName}:ServerName is the MXID domain, not a URL (got '{ServerName}')");

        if (string.IsNullOrWhiteSpace(SenderLocalpart))
            problems.Add($"{SectionName}:SenderLocalpart is required when {SectionName}:Enabled is true");

        if (string.IsNullOrWhiteSpace(AppServiceId))
            problems.Add($"{SectionName}:AppServiceId is required when {SectionName}:Enabled is true");

        // The isolation boundary between environments sharing one homeserver. Enabling Matrix in a
        // new environment without registering an appservice for it must fail here, loudly, rather
        // than start up and collide with another environment's rooms.
        if (string.IsNullOrWhiteSpace(EnvironmentPrefix))
        {
            problems.Add(
                $"{SectionName}:EnvironmentPrefix is required when {SectionName}:Enabled is true. " +
                "All environments share one homeserver, so each needs its own prefix AND its own " +
                "exclusive appservice registration — see docker/synapse/README.md before enabling " +
                "Matrix in a new environment");
        }
        else if (!EnvironmentPrefixPattern().IsMatch(EnvironmentPrefix))
        {
            // Goes into MXID localparts and room aliases, where the allowed character set is
            // narrower than a config string. Rejecting it here beats a 400 from Synapse on the
            // first booking of the day.
            problems.Add(
                $"{SectionName}:EnvironmentPrefix must be 2-16 lowercase letters or digits " +
                $"(got '{EnvironmentPrefix}')");
        }

        if (string.IsNullOrWhiteSpace(AsToken))
            problems.Add($"{SectionName}:AsToken is required when {SectionName}:Enabled is true");

        if (string.IsNullOrWhiteSpace(HsToken))
            problems.Add($"{SectionName}:HsToken is required when {SectionName}:Enabled is true");

        // AsToken and HsToken travel in opposite directions and authenticate different parties.
        // Reusing one value for both means anyone who can call our appservice endpoint also holds
        // the credential that acts as every user on the homeserver.
        if (!string.IsNullOrWhiteSpace(AsToken) && AsToken == HsToken)
            problems.Add($"{SectionName}:AsToken and {SectionName}:HsToken must be different secrets");

        if (TimeoutSeconds <= 0)
            problems.Add($"{SectionName}:TimeoutSeconds must be greater than 0 (got {TimeoutSeconds})");

        if (RoomFreezeDelayHours < 0)
            problems.Add($"{SectionName}:RoomFreezeDelayHours cannot be negative (got {RoomFreezeDelayHours})");

        // A retention window shorter than the freeze delay would purge rooms that are still live.
        if (RoomRetentionDays > 0 && RoomRetentionDays * 24 < RoomFreezeDelayHours)
            problems.Add(
                $"{SectionName}:RoomRetentionDays ({RoomRetentionDays}d) must not be shorter than " +
                $"{SectionName}:RoomFreezeDelayHours ({RoomFreezeDelayHours}h)");

        return problems;
    }

    [GeneratedRegex("^[a-z0-9]{2,16}$")]
    private static partial Regex EnvironmentPrefixPattern();
}
