namespace BeeLogistics.Modules.Payment.Application.Gateways;

/// <summary>
/// The province codes PayMongo accepts for <c>address.state</c> when <c>country</c> is <c>PH</c>.
///
/// <para>
/// Sending anything else fails activation with
/// <c>"state must be a valid Philippine province code"</c> — and because activation is irreversible
/// and the account is frozen afterwards, that is not a mistake worth discovering at the last step.
/// Validated here so the request is rejected before it reaches PayMongo, with a message naming the
/// field.
/// </para>
/// <para>
/// These are ISO 3166-2:PH codes, but the list is PayMongo's, not ISO's — treat it as their
/// vocabulary and re-check it against their docs rather than assuming the two stay in step.
/// </para>
/// </summary>
public static class PhProvinces
{
    /// <summary>Code → display name, in the order PayMongo lists them (Metro Manila first).</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> All = new[]
    {
        ("PH-MNL", "Metro Manila"),
        ("PH-ABR", "Abra"), ("PH-AGN", "Agusan del Norte"), ("PH-AGS", "Agusan del Sur"),
        ("PH-AKL", "Aklan"), ("PH-ALB", "Albay"), ("PH-ANT", "Antique"), ("PH-APA", "Apayao"),
        ("PH-AUR", "Aurora"), ("PH-BAS", "Basilan"), ("PH-BAN", "Bataan"), ("PH-BTN", "Batanes"),
        ("PH-BTG", "Batangas"), ("PH-BEN", "Benguet"), ("PH-BIL", "Biliran"), ("PH-BOH", "Bohol"),
        ("PH-BUK", "Bukidnon"), ("PH-BUL", "Bulacan"), ("PH-CAG", "Cagayan"),
        ("PH-CAN", "Camarines Norte"), ("PH-CAS", "Camarines Sur"), ("PH-CAM", "Camiguin"),
        ("PH-CAP", "Capiz"), ("PH-CAT", "Catanduanes"), ("PH-CAV", "Cavite"), ("PH-CEB", "Cebu"),
        ("PH-COM", "Davao de Oro (Compostela Valley)"), ("PH-NCO", "Cotabato"),
        ("PH-DAV", "Davao del Norte"), ("PH-DAS", "Davao del Sur"), ("PH-DVO", "Davao Occidental"),
        ("PH-DAO", "Davao Oriental"), ("PH-DIN", "Dinagat Islands"), ("PH-EAS", "Eastern Samar"),
        ("PH-GUI", "Guimaras"), ("PH-IFU", "Ifugao"), ("PH-ILN", "Ilocos Norte"),
        ("PH-ILS", "Ilocos Sur"), ("PH-ILI", "Iloilo"), ("PH-ISA", "Isabela"), ("PH-KAL", "Kalinga"),
        ("PH-LUN", "La Union"), ("PH-LAG", "Laguna"), ("PH-LAN", "Lanao del Norte"),
        ("PH-LAS", "Lanao del Sur"), ("PH-LEY", "Leyte"), ("PH-MAG", "Maguindanao"),
        ("PH-MAD", "Marinduque"), ("PH-MAS", "Masbate"), ("PH-MDC", "Mindoro Occidental"),
        ("PH-MDR", "Mindoro Oriental"), ("PH-MSC", "Misamis Occidental"),
        ("PH-MSR", "Misamis Oriental"), ("PH-MOU", "Mountain Province"),
        ("PH-NEC", "Negros Occidental"), ("PH-NER", "Negros Oriental"),
        ("PH-NSA", "Northern Samar"), ("PH-NUE", "Nueva Ecija"), ("PH-NUV", "Nueva Vizcaya"),
        ("PH-PLW", "Palawan"), ("PH-PAM", "Pampanga"), ("PH-PAN", "Pangasinan"),
        ("PH-QUE", "Quezon"), ("PH-QUI", "Quirino"), ("PH-RIZ", "Rizal"), ("PH-ROM", "Romblon"),
        ("PH-WSA", "Samar (Western Samar)"), ("PH-SAR", "Sarangani"), ("PH-SIG", "Siquijor"),
        ("PH-SOR", "Sorsogon"), ("PH-SCO", "South Cotabato"), ("PH-SLE", "Southern Leyte"),
        ("PH-SUK", "Sultan Kudarat"), ("PH-SLU", "Sulu"), ("PH-SUN", "Surigao del Norte"),
        ("PH-SUR", "Surigao del Sur"), ("PH-TAR", "Tarlac"), ("PH-TAW", "Tawi-Tawi"),
        ("PH-ZMB", "Zambales"), ("PH-ZAN", "Zamboanga del Norte"), ("PH-ZAS", "Zamboanga del Sur"),
        ("PH-ZSI", "Zamboanga Sibugay"),
    };

    private static readonly HashSet<string> Codes =
        new(All.Select(p => p.Code), StringComparer.OrdinalIgnoreCase);

    public static bool IsValid(string? code)
        => !string.IsNullOrWhiteSpace(code) && Codes.Contains(code.Trim());

    public static string? NameFor(string? code)
        => All.FirstOrDefault(p => string.Equals(p.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase)).Name;
}
