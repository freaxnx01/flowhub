namespace FlowHub.Skills.Bridge;

/// <summary>
/// Bound from configuration section <c>Skills:Bridge</c>. The integration fails closed
/// during DI when <see cref="BaseUrl"/> or <see cref="ApiToken"/> is empty, so FlowHub can
/// merge ahead of the bridge-serve deploy.
/// </summary>
public sealed class BridgeOptions
{
    public const string SectionName = "Skills:Bridge";

    public string? BaseUrl { get; set; }

    public string? ApiToken { get; set; }

    /// <summary>How long the alias catalog is cached before re-fetching from bridge.</summary>
    public TimeSpan CatalogTtl { get; set; } = TimeSpan.FromMinutes(5);
}
