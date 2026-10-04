using BeeLogistics.Modules.Payment.Application.Banks;
using BeeLogistics.Modules.Payment.Infrastructure.Services;
using Xunit;

namespace BeeLogistics.Tests.Payments;

public class PhBankCatalogTests
{
    [Fact]
    public void Catalog_loads_and_is_not_empty()
    {
        Assert.NotEmpty(PhBankCatalog.All);
        Assert.NotEmpty(PhBankCatalog.Payable);
    }

    [Fact]
    public void Every_payable_institution_has_a_bic_and_at_least_one_rail()
    {
        foreach (var bank in PhBankCatalog.Payable)
        {
            Assert.False(string.IsNullOrWhiteSpace(bank.Bic), $"{bank.Code} is payable but has no BIC");
            Assert.True(bank.Instapay || bank.Pesonet, $"{bank.Code} supports neither rail");
        }
    }

    [Fact]
    public void Codes_are_unique()
    {
        var duplicates = PhBankCatalog.All
            .GroupBy(b => b.Code, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Theory]
    // The friendly codes the database has been storing since the Xendit era, paired with
    // the BIC each historically resolved to. If any stops resolving, saved withdrawal
    // methods created before the catalog silently break.
    //
    // Asserted as a prefix, not an exact match: PayMongo returns the full 11-character
    // BIC (BNORPHMMXXX) where the hand-written map carried the 8-character institution
    // code (BNORPHMM), and the last three characters are branch-specific — PNB's InstaPay
    // code is PNBMPHMMTOD, not ...XXX. The first eight identify the institution, which is
    // what "still resolves to the same bank" actually means.
    [InlineData("BPI", "BOPIPHMM")]
    [InlineData("BDO", "BNORPHMM")]
    [InlineData("UBP", "UBPHPHMM")]
    [InlineData("UNIONBANK", "UBPHPHMM")]
    [InlineData("GCASH", "GXCHPHM2")]
    [InlineData("SEC", "SETCPHMM")]
    [InlineData("SECURITY", "SETCPHMM")]
    [InlineData("SECURITY BANK", "SETCPHMM")]
    [InlineData("MAYA", "PAPHPHM1")]
    [InlineData("LANDBANK", "TLBPPHMM")]
    [InlineData("METROBANK", "MBTCPHMM")]
    [InlineData("MBTC", "MBTCPHMM")]
    [InlineData("PNB", "PNBMPHMM")]
    [InlineData("RCBC", "RCBCPHMM")]
    [InlineData("CHINABANK", "CHBKPHMM")]
    [InlineData("EASTWEST", "EWBCPHMM")]
    public void Legacy_codes_still_resolve(string storedCode, string institutionBic)
    {
        Assert.True(PhBankCatalog.TryResolve(storedCode, out var bank), $"'{storedCode}' no longer resolves");
        Assert.StartsWith(institutionBic, bank.Bic);
        Assert.StartsWith(institutionBic, PayMongoBankCodeMap.Normalize(storedCode));
    }

    [Fact]
    public void Rail_specific_bics_are_used_where_they_differ()
    {
        // PNB is listed once per rail under different codes. Sending the InstaPay code on
        // PESONet is the kind of mistake that fails at the receiving bank, not at us.
        Assert.True(PhBankCatalog.TryResolve("PNB", out var pnb));
        Assert.Equal("PNBMPHMMTOD", pnb.BicFor(TransferRail.Instapay));
        Assert.Equal("PNBMPHMMXXX", pnb.BicFor(TransferRail.Pesonet));
        Assert.Equal("PNBMPHMMTOD", PayMongoBankCodeMap.Normalize("PNB", TransferRail.Instapay));
        Assert.Equal("PNBMPHMMXXX", PayMongoBankCodeMap.Normalize("PNB", TransferRail.Pesonet));
    }

    [Fact]
    public void Single_bic_institutions_return_it_for_either_rail()
    {
        Assert.True(PhBankCatalog.TryResolve("BDO", out var bdo));
        Assert.Equal(bdo.Bic, bdo.BicFor(TransferRail.Instapay));
        Assert.Equal(bdo.Bic, bdo.BicFor(TransferRail.Pesonet));
    }

    [Fact]
    public void Consumer_brands_are_named_for_the_brand_not_the_legal_entity()
    {
        // PayMongo returns "G-Xchange, Inc."; nobody searching a picker types that.
        Assert.True(PhBankCatalog.TryResolve("GCASH", out var gcash));
        Assert.Equal("GCash", gcash.Name);
        Assert.Equal("G-Xchange, Inc.", gcash.LegalName);
    }

    [Fact]
    public void Xendit_channel_codes_resolve_through_the_catalog()
        => Assert.StartsWith("BOPIPHMM", PayMongoBankCodeMap.Normalize("PH_BPI"));

    [Fact]
    public void Institutions_without_a_bic_are_not_payable()
    {
        // The seeded catalog carries every institution PayMongo publishes, but only the
        // verified BICs. An entry we cannot address must never reach a transfer payload.
        foreach (var bank in PhBankCatalog.All.Where(b => b.Bic is null))
        {
            Assert.False(PhBankCatalog.TryResolve(bank.Code, out _), $"{bank.Code} has no BIC but resolves");
            Assert.DoesNotContain(bank, PhBankCatalog.Payable);
        }
    }

    [Fact]
    public void Unknown_code_does_not_resolve()
        => Assert.False(PhBankCatalog.TryResolve("NOT A BANK", out _));

    [Fact]
    public void Instapay_only_institutions_cap_at_the_instapay_limit()
    {
        foreach (var bank in PhBankCatalog.Payable.Where(b => b.Instapay && !b.Pesonet))
            Assert.Equal(PhBank.InstapayLimit, bank.MaxAmount);
    }

    [Fact]
    public void ForRail_only_returns_institutions_that_support_it()
    {
        Assert.All(PhBankCatalog.ForRail(TransferRail.Instapay), b => Assert.True(b.Instapay));
        Assert.All(PhBankCatalog.ForRail(TransferRail.Pesonet), b => Assert.True(b.Pesonet));
    }

    [Fact]
    public void Gcash_is_classified_as_an_ewallet()
    {
        Assert.True(PhBankCatalog.TryResolve("GCASH", out var gcash));
        Assert.True(gcash.IsEwallet);
        Assert.True(gcash.Instapay);
    }
}
