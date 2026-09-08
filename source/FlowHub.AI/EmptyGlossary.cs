using FlowHub.Core.Classification;

namespace FlowHub.AI;

/// <summary>Registered via TryAddSingleton so an unconfigured deployment behaves as today.</summary>
internal sealed class EmptyGlossary : IGlossary
{
    public Task<GlossarySnapshot> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(GlossarySnapshot.Empty);
}
