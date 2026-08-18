namespace FlowHub.Core.Skills;

/// <summary>
/// Driven port exposing the set of repo aliases known to the <c>bridge</c> service
/// (sourced from its <c>GET /api/repos</c> catalog). Aliases are lowercased. The
/// classifier consults this to short-circuit a leading alias token to the Bridge skill.
/// Implementations must be resilient: on a fetch failure return the last-known set (or
/// empty) rather than throwing, so classification never breaks.
/// </summary>
public interface IBridgeCatalog
{
    Task<IReadOnlySet<string>> GetAliasesAsync(CancellationToken cancellationToken);
}
