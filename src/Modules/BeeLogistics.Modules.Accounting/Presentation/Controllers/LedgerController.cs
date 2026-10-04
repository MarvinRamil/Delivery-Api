using BeeLogistics.Modules.Accounting.Application.Interfaces;
using BeeLogistics.Modules.Accounting.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BeeLogistics.Modules.Accounting.Presentation.Controllers;

/// <summary>
/// Read-only API for ledger entries and balances. Backoffice only; no write endpoints.
/// </summary>
[ApiController]
[Route("api/ledger")]
[Authorize(Policy = "Backoffice")]
public class LedgerController : ControllerBase
{
    private readonly ILedgerEntryRepository _repository;
    private readonly ISalesEntryRepository _salesRepository;

    public LedgerController(ILedgerEntryRepository repository, ISalesEntryRepository salesRepository)
    {
        _repository = repository;
        _salesRepository = salesRepository;
    }

    /// <summary>
    /// Get ledger entries with optional filters. Limit capped at 500.
    /// </summary>
    [HttpGet("entries")]
    public async Task<IActionResult> GetEntries(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? accountCode,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        var capped = Math.Clamp(limit, 1, 500);
        var entries = await _repository.GetAsync(from, to, accountCode, capped, ct);
        var dtos = entries.Select(e => new LedgerEntryDto(
            e.Id,
            e.AccountCode,
            e.IsDebit,
            e.Amount,
            e.Currency,
            e.ReferenceType,
            e.ReferenceId,
            e.Description,
            e.CreatedAtUtc
        )).ToList();
        return Ok(dtos);
    }

    /// <summary>
    /// Get ledger entries by reference (e.g. referenceType=Withdrawal, referenceId=withdrawal Guid).
    /// </summary>
    [HttpGet("entries/by-reference")]
    public async Task<IActionResult> GetByReference(
        [FromQuery] string referenceType,
        [FromQuery] string referenceId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(referenceType) || string.IsNullOrWhiteSpace(referenceId))
            return BadRequest("referenceType and referenceId are required.");
        var entries = await _repository.GetByReferenceAsync(referenceType, referenceId, ct);
        var dtos = entries.Select(e => new LedgerEntryDto(
            e.Id,
            e.AccountCode,
            e.IsDebit,
            e.Amount,
            e.Currency,
            e.ReferenceType,
            e.ReferenceId,
            e.Description,
            e.CreatedAtUtc
        )).ToList();
        return Ok(dtos);
    }

    /// <summary>
    /// Get current balance per account code (DriverPersonalWallet, PendingPayout, XenditOut, etc.).
    /// </summary>
    [HttpGet("balances")]
    public async Task<IActionResult> GetBalances(CancellationToken ct = default)
    {
        var codes = new[]
        {
            AccountCode.DriverPersonalWallet,
            AccountCode.DriverTopUpWallet,
            AccountCode.PendingPayout,
            AccountCode.XenditOut,
            AccountCode.PlatformRevenue,
            AccountCode.CustomerPayment
        };
        var balances = new List<LedgerBalanceDto>();
        foreach (var code in codes)
        {
            var balance = await _repository.GetBalanceAsync(code, ct);
            balances.Add(new LedgerBalanceDto(code, balance));
        }
        return Ok(balances);
    }

    /// <summary>
    /// Get sales (fast table) with optional filters. Cash and cashless. Limit capped at 500.
    /// </summary>
    [HttpGet("sales")]
    public async Task<IActionResult> GetSales(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? paymentMethod,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        var capped = Math.Clamp(limit, 1, 500);
        var entries = await _salesRepository.GetAsync(from, to, paymentMethod, capped, ct);
        var dtos = entries.Select(e => new SalesEntryDto(
            e.Id,
            e.BookingId,
            e.CompletedAtUtc,
            e.Amount,
            e.Currency,
            e.PaymentMethod,
            e.DriverId,
            e.CustomerId,
            e.PlatformCommissionAmount,
            e.DriverAmount,
            e.CreatedAtUtc
        )).ToList();
        return Ok(dtos);
    }
}

/// <summary>
/// DTO for a single ledger entry (no PII or sensitive data).
/// </summary>
public record LedgerEntryDto(
    Guid Id,
    string AccountCode,
    bool IsDebit,
    decimal Amount,
    string Currency,
    string ReferenceType,
    string ReferenceId,
    string? Description,
    DateTime CreatedAtUtc);

/// <summary>
/// DTO for account balance.
/// </summary>
public record LedgerBalanceDto(string AccountCode, decimal Balance);

/// <summary>
/// DTO for a sales entry (fast table).
/// </summary>
public record SalesEntryDto(
    Guid Id,
    Guid BookingId,
    DateTime CompletedAtUtc,
    decimal Amount,
    string Currency,
    string PaymentMethod,
    Guid DriverId,
    Guid CustomerId,
    decimal PlatformCommissionAmount,
    decimal DriverAmount,
    DateTime CreatedAtUtc);
