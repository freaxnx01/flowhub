using System.Security.Cryptography;
using System.Text;
using FlowHub.Core.Captures;
using FlowHub.Core.Skills;
using Microsoft.Extensions.Logging;

namespace FlowHub.AI;

/// <summary>
/// Brings the repo-embedding store in line with the bridge catalogue. Only repositories
/// whose name+description hash changed are re-embedded, so a steady-state refresh makes
/// zero embedding calls. Best-effort throughout: a null embedding (service unconfigured
/// or failing) skips that repository rather than failing the sync.
/// </summary>
internal sealed partial class RepoEmbeddingSynchronizer
{
    private readonly IBridgeCatalog _catalog;
    private readonly IRepoEmbeddingStore _store;
    private readonly IEmbeddingService _embeddings;
    private readonly ILogger<RepoEmbeddingSynchronizer> _log;

    public RepoEmbeddingSynchronizer(
        IBridgeCatalog catalog,
        IRepoEmbeddingStore store,
        IEmbeddingService embeddings,
        ILogger<RepoEmbeddingSynchronizer> log)
    {
        _catalog = catalog;
        _store = store;
        _embeddings = embeddings;
        _log = log;
    }

    /// <summary>Embedding input for a repository — the name carries real signal (e.g. the "game-" prefix).</summary>
    internal static string TextOf(BridgeRepo repo) => $"{repo.Name}\n{repo.Desc}".TrimEnd();

    internal static string HashOf(BridgeRepo repo) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TextOf(repo))));

    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        var repos = await _catalog.GetReposAsync(cancellationToken);
        if (repos.Count == 0)
        {
            return;
        }

        var known = await _store.GetHashesAsync(cancellationToken);

        foreach (var repo in repos)
        {
            var hash = HashOf(repo);
            if (known.TryGetValue(repo.Name, out var existing) && string.Equals(existing, hash, StringComparison.Ordinal))
            {
                continue;
            }

            var embedding = await _embeddings.GenerateAsync(TextOf(repo), cancellationToken);
            if (embedding is null)
            {
                LogEmbeddingUnavailable(repo.Name);
                continue;
            }

            await _store.UpsertAsync(repo.Name, hash, embedding, cancellationToken);
        }

        await _store.RemoveMissingAsync(repos.Select(r => r.Name).ToList(), cancellationToken);
    }

    [LoggerMessage(EventId = 3060, Level = LogLevel.Debug,
        Message = "No embedding available for repo {RepoName}; skipping")]
    private partial void LogEmbeddingUnavailable(string repoName);
}
