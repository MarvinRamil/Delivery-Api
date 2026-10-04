using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BeeLogistics.Modules.Payment.Application.Banks;
using BeeLogistics.Shared.DTOs;
using BeeLogistics.Shared.Presentation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BeeLogistics.Modules.Payment.Presentation.Controllers;

/// <summary>
/// The banks and e-wallets a driver can withdraw to. Served from the embedded
/// <see cref="PhBankCatalog"/> — no PayMongo round-trip per request, no secrets — so the
/// apps can render the withdrawal picker without every client hard-coding a bank list.
/// </summary>
[Route("api/payments/banks")]
public class PaymentBanksController : BaseController
{
    /// <summary>
    /// Lists payable institutions, newest catalog wins. <paramref name="rail"/> narrows to
    /// the ones that can receive on that rail; omit it for everything payable.
    /// </summary>
    [HttpGet]
    [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Client)]
    public IActionResult GetBanks([FromQuery] string? rail = null)
    {
        IReadOnlyList<PhBank> banks;

        if (string.IsNullOrWhiteSpace(rail))
        {
            banks = PhBankCatalog.Payable;
        }
        else if (Enum.TryParse<TransferRail>(rail.Trim(), ignoreCase: true, out var parsed))
        {
            banks = PhBankCatalog.ForRail(parsed);
        }
        else
        {
            return BadRequest(ApiResponse.Fail("rail must be 'instapay' or 'pesonet'"));
        }

        var payload = banks
            .OrderBy(b => b.IsEwallet ? 0 : 1)  // e-wallets first — how the picker groups them
            .ThenBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(b => new BankDto(b.Code, b.Name, b.LegalName, b.Bic!, b.Instapay, b.Pesonet, b.Type, b.MaxAmount))
            .ToList();

        // The catalog only changes when the repo does, so a content ETag lets the apps
        // revalidate their 24h cache with a 304 instead of re-downloading ~150 rows.
        var etag = ComputeETag(payload);
        if (Request.Headers.IfNoneMatch.Any(v => v == etag))
            return StatusCode(StatusCodes.Status304NotModified);

        Response.Headers.ETag = etag;
        return Ok(ApiResponse<IReadOnlyList<BankDto>>.Ok(payload));
    }

    private static string ComputeETag(IReadOnlyList<BankDto> banks)
    {
        var sb = new StringBuilder();
        foreach (var b in banks)
            sb.Append(b.Code).Append(':').Append(b.Bic).Append(':')
              .Append(b.Instapay ? '1' : '0').Append(b.Pesonet ? '1' : '0').Append(';');

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return $"\"{Convert.ToHexString(hash, 0, 8).ToLower(CultureInfo.InvariantCulture)}\"";
    }

    /// <param name="Code">Stable identifier the apps send back as <c>bankCode</c>.</param>
    /// <param name="Bic">PayMongo's destination BIC. Never null here — unpayable entries are filtered out.</param>
    /// <param name="MaxAmount">Per-transaction ceiling of the fastest rail this institution supports.</param>
    public record BankDto(
        string Code,
        string Name,
        /// <summary>PayMongo's registered name, when the display name differs (GCash / "G-Xchange, Inc.").</summary>
        string? LegalName,
        string Bic,
        bool Instapay,
        bool Pesonet,
        string Type,
        decimal MaxAmount);
}
