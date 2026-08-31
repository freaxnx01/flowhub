using FlowHub.Core.Skills;
using Microsoft.Extensions.DependencyInjection;

namespace FlowHub.AI;

/// <summary>
/// Adapter that lets the singleton <see cref="RepoResolver"/> use the scoped
/// <see cref="IRepoEmbeddingStore"/> (backed by the EF <c>DbContext</c>). Each method
/// call opens a fresh DI scope, resolves the real store from it, and disposes the scope
/// when the call returns. This is registered as a non-<see cref="IRepoEmbeddingStore"/>
/// singleton so it does not shadow the scoped store used elsewhere in the app.
/// </summary>
internal sealed class ScopedRepoEmbeddingStore : IRepoEmbeddingStore
{
    private readonly IServiceScopeFactory _scopes;

    public ScopedRepoEmbeddingStore(IServiceScopeFactory scopes) => _scopes = scopes;

    public async Task<IReadOnlyDictionary<string, string>> GetHashesAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var inner = scope.ServiceProvider.GetRequiredService<IRepoEmbeddingStore>();
        return await inner.GetHashesAsync(cancellationToken);
    }

    public async Task UpsertAsync(string repoName, string contentHash, float[] embedding, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var inner = scope.ServiceProvider.GetRequiredService<IRepoEmbeddingStore>();
        await inner.UpsertAsync(repoName, contentHash, embedding, cancellationToken);
    }

    public async Task RemoveMissingAsync(IReadOnlyCollection<string> keepRepoNames, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var inner = scope.ServiceProvider.GetRequiredService<IRepoEmbeddingStore>();
        await inner.RemoveMissingAsync(keepRepoNames, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> NearestAsync(float[] queryEmbedding, int limit, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var inner = scope.ServiceProvider.GetRequiredService<IRepoEmbeddingStore>();
        return await inner.NearestAsync(queryEmbedding, limit, cancellationToken);
    }
}
