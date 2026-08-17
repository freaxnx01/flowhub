using FlowHub.Core.Skills;

namespace FlowHub.AI;

/// <summary>
/// No-op <see cref="IBridgeCatalog"/> registered when the Bridge skill is unconfigured,
/// so the container resolves the classifiers cleanly. Returns an empty alias set, which
/// makes <c>BridgeAliasMatcher</c> never match — classification proceeds unchanged.
/// The real <c>BridgeCatalog</c> (FlowHub.Skills) overrides this when Bridge is configured.
/// </summary>
internal sealed class EmptyBridgeCatalog : IBridgeCatalog
{
    private static readonly Task<IReadOnlySet<string>> Empty =
        Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));

    public Task<IReadOnlySet<string>> GetAliasesAsync(CancellationToken cancellationToken) => Empty;
}
