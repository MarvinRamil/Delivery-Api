using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeeLogistics.Modules.Payment.Application.Banks;

/// <summary>Transfer rail a payout can travel on.</summary>
public enum TransferRail
{
    Instapay,
    Pesonet
}

/// <summary>
/// One receiving institution. <see cref="Code"/> is the stable identifier stored on
/// withdrawal records and sent by the apps; <see cref="Bic"/> is what PayMongo wants as
/// <c>destination_account.bic</c>. Bic is null for institutions PayMongo publishes by
/// name only — those are withheld from the catalog until the fetch script fills them in.
/// </summary>
public sealed record PhBank(
    string Code,
    string Name,
    string? Bic,
    string? InstapayBic,
    string? PesonetBic,
    bool Instapay,
    bool Pesonet,
    string Type,
    string? LegalName = null)
{
    public bool IsEwallet => string.Equals(Type, "ewallet", StringComparison.OrdinalIgnoreCase);

    public bool Supports(TransferRail rail) => rail == TransferRail.Instapay ? Instapay : Pesonet;

    /// <summary>
    /// The BIC to address a transfer on a specific rail. A few institutions are listed
    /// once per rail under different codes — PNB is PNBMPHMMTOD on InstaPay and
    /// PNBMPHMMXXX on PESONet — so the rail must be known before the BIC is chosen.
    /// Falls back to the other rail's code for the majority that use one BIC for both.
    /// </summary>
    public string? BicFor(TransferRail rail)
        => (rail == TransferRail.Instapay ? InstapayBic : PesonetBic)
           ?? (rail == TransferRail.Instapay ? PesonetBic : InstapayBic);

    /// <summary>Per-transaction ceiling of the fastest rail this institution can receive on.</summary>
    public decimal MaxAmount => Instapay ? InstapayLimit : PesonetLimit;

    public const decimal InstapayLimit = 50_000m;
    public const decimal PesonetLimit = 10_000_000m;
}

/// <summary>
/// The Philippine receiving institutions PayMongo can pay out to, loaded once from the
/// embedded PhBanks.json. This is the single source of truth: <see cref="PayMongoBankCodeMap"/>
/// resolves through it, GET /api/payments/banks serves it, and the driver app's
/// shared/constants/banks.ts is generated from the same file.
///
/// Entries without a BIC are parsed but excluded from <see cref="Payable"/> — a guessed
/// BIC would route a driver's payout to the wrong institution.
/// </summary>
public static class PhBankCatalog
{
    private const string ResourceName = "BeeLogistics.Modules.Payment.Application.Banks.PhBanks.json";

    private static readonly Lazy<Catalog> Loaded = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Every institution in the catalog, including those still missing a BIC.</summary>
    public static IReadOnlyList<PhBank> All => Loaded.Value.All;

    /// <summary>Institutions that can actually receive a transfer today (BIC known).</summary>
    public static IReadOnlyList<PhBank> Payable => Loaded.Value.Payable;

    /// <summary>How many institutions are still awaiting a BIC from fetch-paymongo-banks.sh.</summary>
    public static int MissingBicCount => Loaded.Value.All.Length - Loaded.Value.Payable.Length;

    /// <summary>
    /// Legacy stored code -> catalog code. The database has carried these friendly
    /// spellings since the Xendit era, so records written before this catalog existed
    /// must keep resolving. Only spellings that differ from a catalog code need an entry.
    /// </summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UNIONBANK"] = "UBP",
        ["SECURITY"] = "SEC",
        ["SECURITY BANK"] = "SEC",
        ["MBTC"] = "METROBANK",
    };

    /// <summary>Exact catalog-code lookup. Use <see cref="TryResolve"/> for codes off a record.</summary>
    public static bool TryGet(string code, out PhBank bank)
    {
        if (!string.IsNullOrWhiteSpace(code))
            return Loaded.Value.ByCode.TryGetValue(code.Trim(), out bank!);

        bank = null!;
        return false;
    }

    /// <summary>
    /// Resolves a bank code as stored on a withdrawal record or sent by an app —
    /// catalog code, legacy alias, or Xendit-style channel code (PH_BPI) — to an
    /// institution we can actually pay out to. False when the code is unknown or the
    /// institution has no BIC yet, which are the same thing from a caller's point of view.
    /// </summary>
    public static bool TryResolve(string? bankCode, out PhBank bank)
    {
        bank = null!;
        if (string.IsNullOrWhiteSpace(bankCode)) return false;

        var code = bankCode.Trim();
        if (Aliases.TryGetValue(code, out var aliased))
        {
            code = aliased;
        }
        else if (code.StartsWith("PH_", StringComparison.OrdinalIgnoreCase))
        {
            var stripped = code[3..];
            code = Aliases.TryGetValue(stripped, out var viaAlias) ? viaAlias : stripped;
        }

        return TryGet(code, out bank) && bank.Bic is not null;
    }

    /// <summary>Catalog codes plus the legacy aliases, for cross-provider parity tests.</summary>
    public static IReadOnlyCollection<string> KnownCodes
        => Payable.Select(b => b.Code).Concat(Aliases.Keys).ToList();

    /// <summary>Institutions that can receive on the given rail and are payable today.</summary>
    public static IReadOnlyList<PhBank> ForRail(TransferRail rail)
        => Payable.Where(b => b.Supports(rail)).ToImmutableArray();

    private static Catalog Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' is missing.");

        var file = JsonSerializer.Deserialize<CatalogFile>(stream, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("PhBanks.json could not be parsed.");

        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        var all = file.Banks
            .Select(b => new PhBank(b.Code, b.Name, Clean(b.Bic), Clean(b.InstapayBic), Clean(b.PesonetBic),
                b.Instapay, b.Pesonet, b.Type, Clean(b.LegalName)))
            .ToImmutableArray();

        if (all.Length == 0)
            throw new InvalidOperationException("PhBanks.json contains no institutions.");

        return new Catalog(
            all,
            all.Where(b => b.Bic is not null).ToImmutableArray(),
            all.ToImmutableDictionary(b => b.Code, StringComparer.OrdinalIgnoreCase));
    }

    private sealed record Catalog(
        ImmutableArray<PhBank> All,
        ImmutableArray<PhBank> Payable,
        ImmutableDictionary<string, PhBank> ByCode);

    private sealed class CatalogFile
    {
        [JsonPropertyName("banks")]
        public List<BankRecord> Banks { get; set; } = new();
    }

    private sealed class BankRecord
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string? LegalName { get; set; }
        public string? Bic { get; set; }
        public string? InstapayBic { get; set; }
        public string? PesonetBic { get; set; }
        public bool Instapay { get; set; }
        public bool Pesonet { get; set; }
        public string Type { get; set; } = "bank";
    }
}
