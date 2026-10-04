using System.Diagnostics;
using BeeLogistics.Modules.Messaging.Infrastructure;
using Xunit;

namespace BeeLogistics.Tests.Messaging;

/// <summary>
/// The hs_token check is the only thing standing between the appservice endpoint and anyone who
/// finds it. A forged transaction writes arbitrary messages into the admin transcript, attributed
/// to whichever party the attacker names — so this is worth pinning properly.
/// </summary>
public class MatrixTransactionAuthenticatorTests
{
    // Shaped like a real hs_token - 64 hex characters - so the comparison under test gets a
    // realistic input. That shape is exactly what trips gitleaks' generic-api-key entropy rule,
    // hence the marker; the value is fixed, public, and authenticates nothing.
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"; // gitleaks:allow

    [Fact]
    public void The_configured_token_is_accepted()
    {
        Assert.True(MatrixTransactionAuthenticator.IsAuthentic(Token, Token));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("wrong")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde0")]  // last char differs
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdefX")] // suffixed
    [InlineData(" 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")] // leading space
    public void Anything_else_is_rejected(string? presented)
    {
        Assert.False(MatrixTransactionAuthenticator.IsAuthentic(presented, Token));
    }

    [Fact]
    public void An_unconfigured_expected_token_rejects_everything_including_the_empty_string()
    {
        // Fail closed. If HsToken were somehow blank, an empty Authorization header must not match
        // it and turn the endpoint into an open door.
        Assert.False(MatrixTransactionAuthenticator.IsAuthentic("", ""));
        Assert.False(MatrixTransactionAuthenticator.IsAuthentic(null, ""));
        Assert.False(MatrixTransactionAuthenticator.IsAuthentic("anything", ""));
    }

    [Theory]
    [InlineData("Bearer abc", "abc")]
    [InlineData("bearer abc", "abc")]          // header schemes are case-insensitive
    [InlineData("Bearer  abc  ", "abc")]
    [InlineData("Basic abc", null)]            // wrong scheme
    [InlineData("abc", null)]                  // no scheme
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Bearer_extraction_handles_the_shapes_that_actually_arrive(string? header, string? expected)
    {
        Assert.Equal(expected, MatrixTransactionAuthenticator.ExtractBearer(header));
    }

    [Fact]
    public void Comparison_does_not_short_circuit_on_the_first_differing_byte()
    {
        // A naive string compare returns faster for a token that differs at the end than one that
        // differs at the start, which leaks the token a byte at a time. This is a smoke test, not
        // a rigorous timing proof — it would catch someone swapping in ==, which is the realistic
        // regression.
        var differsFirst  = "X" + Token[1..];
        var differsLast   = Token[..^1] + "X";

        var tFirst = Time(() => MatrixTransactionAuthenticator.IsAuthentic(differsFirst, Token));
        var tLast  = Time(() => MatrixTransactionAuthenticator.IsAuthentic(differsLast, Token));

        Assert.False(MatrixTransactionAuthenticator.IsAuthentic(differsFirst, Token));
        Assert.False(MatrixTransactionAuthenticator.IsAuthentic(differsLast, Token));

        // Deliberately loose: CI machines are noisy and a tight bound would flake.
        var ratio = (double)Math.Max(tFirst, tLast) / Math.Max(1, Math.Min(tFirst, tLast));
        Assert.True(ratio < 50, $"suspicious timing asymmetry: {tFirst} vs {tLast} ticks");
    }

    private static long Time(Action action)
    {
        for (var i = 0; i < 200; i++) action();   // warm up the JIT
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 20_000; i++) action();
        return sw.ElapsedTicks;
    }
}
