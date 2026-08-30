namespace FlowHub.Core.Skills;

/// <summary>
/// Driven port for the per-repository embedding cache backing repo inference.
/// Keyed by repository name; <c>ContentHash</c> lets a catalogue refresh skip
/// re-embedding repositories whose name and description are unchanged.
/// </summary>
public interface IRepoEmbeddingStore
{
    Task<IReadOnlyDictionary<string, string>> GetHashesAsync(CancellationToken cancellationToken);

    Task UpsertAsync(string repoName, string contentHash, float[] embedding, CancellationToken cancellationToken);

    Task RemoveMissingAsync(IReadOnlyCollection<string> keepRepoNames, CancellationToken cancellationToken);

    /// <returns>Repository names ordered nearest-first.</returns>
    Task<IReadOnlyList<string>> NearestAsync(float[] queryEmbedding, int limit, CancellationToken cancellationToken);
}
