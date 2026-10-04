using Microsoft.Extensions.Configuration;

namespace BeeLogistics.Shared.Infrastructure.Configuration;

/// <summary>
/// Collects the local-override keys that are allowed to win over the Vault overlay in Development
/// (see the dev-override block in Program.cs). Split out so the filtering rule is unit-testable.
///
/// The filter must reject blank values, not just nulls. The source config includes environment
/// variables, and the deploy scripts pass keys unconditionally with an empty default
/// (`-e "DigitalOcean__Endpoint=${DIGITALOCEAN_ENDPOINT:-}"`). Those arrive as present-but-empty
/// and, under the old `kv.Value is not null` test, were re-applied on top of Vault - blanking a
/// perfectly good secret and taking file storage down to NoOp. Issue #42.
/// </summary>
public static class DevelopmentConfigOverrides
{
    /// <summary>
    /// Returns every non-blank key under <paramref name="sections"/> of <paramref name="localConfig"/>,
    /// with full configuration paths, ready for AddInMemoryCollection. Sections absent from the local
    /// config contribute nothing, so anything not overridden locally keeps its Vault value.
    /// </summary>
    public static List<KeyValuePair<string, string?>> Collect(IConfiguration localConfig, IEnumerable<string> sections)
        => sections
            .SelectMany(section => localConfig.GetSection(section)
                .AsEnumerable(makePathsRelative: false)
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Value)))
            .ToList();
}
