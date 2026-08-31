using FlowHub.Core.Captures;
using FlowHub.Core.Classification;
using FlowHub.Core.Skills;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace FlowHub.AI;

/// <summary>Outcome of repo inference. <c>Repo</c> is always a real target.</summary>
internal sealed record RepoResolution(string Repo, BridgeAction Action, string? Title, string? Body);

/// <summary>
/// Picks the target repository for a Bridge-classified capture that carries no alias:
/// cosine top-5 over the repo embedding store, then one LLM call that may abstain.
/// An abstain becomes an idea in <c>ideas-lab</c>; an unresolvable capture returns null
/// so the pipeline parks it. Never throws.
/// </summary>
internal sealed partial class RepoResolver
{
    internal const string IdeaFallbackRepo = "ideas-lab";
    private const int ShortlistSize = 5;

    private readonly IChatClient _chat;
    private readonly IBridgeCatalog _catalog;
    private readonly IRepoEmbeddingStore _store;
    private readonly IEmbeddingService _embeddings;
    private readonly ChatOptions _options;
    private readonly RepoEmbeddingSynchronizer _sync;
    private readonly ILogger<RepoResolver> _log;

    public RepoResolver(
        IChatClient chat,
        IBridgeCatalog catalog,
        IRepoEmbeddingStore store,
        IEmbeddingService embeddings,
        ChatOptions options,
        RepoEmbeddingSynchronizer sync,
        ILogger<RepoResolver> log)
    {
        _chat = chat;
        _catalog = catalog;
        _store = store;
        _embeddings = embeddings;
        _options = options;
        _sync = sync;
        _log = log;
    }

    public async Task<RepoResolution?> ResolveAsync(string content, CancellationToken cancellationToken)
    {
        try
        {
            await _sync.SyncAsync(cancellationToken);

            var repos = await _catalog.GetReposAsync(cancellationToken);
            if (repos.Count == 0)
            {
                return null;
            }

            var shortlist = await ShortlistAsync(content, repos, cancellationToken);
            if (shortlist.Count == 0)
            {
                return null;
            }

            var response = await _chat.GetResponseAsync<AiRepoConfirmResponse>(
                AiPrompts.BuildRepoConfirmMessages(
                    content, shortlist.Select(r => (r.Name, r.Desc)).ToList()),
                _options,
                cancellationToken: cancellationToken);

            if (!response.TryGetResult(out var payload))
            {
                return null;
            }

            var action = string.Equals(payload.Action, "issue", StringComparison.Ordinal)
                ? BridgeAction.Issue
                : BridgeAction.Idea;

            if (string.IsNullOrWhiteSpace(payload.Repo))
            {
                // No existing home — typically a request to create a project.
                return new RepoResolution(IdeaFallbackRepo, BridgeAction.Idea, payload.Title, payload.Body);
            }

            // The catalogue is authoritative: only a name we offered is acceptable.
            if (!shortlist.Any(r => string.Equals(r.Name, payload.Repo, StringComparison.Ordinal)))
            {
                LogUnlistedRepo(payload.Repo);
                return null;
            }

            return new RepoResolution(payload.Repo, action, payload.Title, payload.Body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogResolveFailed(ex.GetType().Name);
            return null;
        }
    }

    private async Task<IReadOnlyList<BridgeRepo>> ShortlistAsync(
        string content, IReadOnlyList<BridgeRepo> repos, CancellationToken cancellationToken)
    {
        var embedding = await _embeddings.GenerateAsync(content, cancellationToken);
        if (embedding is not null)
        {
            var names = await _store.NearestAsync(embedding, ShortlistSize, cancellationToken);
            var byName = repos.ToDictionary(r => r.Name, StringComparer.Ordinal);
            var hits = names
                .Where(byName.ContainsKey)
                .Select(n => byName[n])
                .ToList();

            if (hits.Count > 0)
            {
                return hits;
            }
        }

        return LexicalShortlist(content, repos);
    }

    /// <summary>
    /// Fallback when embeddings are unconfigured or the store is empty. Deliberately
    /// crude — it only has to produce plausible candidates for the model to judge.
    /// </summary>
    private static List<BridgeRepo> LexicalShortlist(string content, IReadOnlyList<BridgeRepo> repos)
    {
        var terms = content
            .Split([' ', '\t', '\n', ':', ',', '.', '/', '-', '(', ')', '?', '!'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 3)
            .Select(t => t.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        return repos
            .Select(r => (Repo: r, Score: Score(r, terms)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Repo.LastUsed ?? DateTimeOffset.MinValue)
            .Take(ShortlistSize)
            .Select(x => x.Repo)
            .ToList();

        static int Score(BridgeRepo repo, HashSet<string> terms)
        {
            var text = $"{repo.Name} {repo.Desc}".ToLowerInvariant();
            return terms.Count(t => text.Contains(t, StringComparison.Ordinal));
        }
    }

    [LoggerMessage(EventId = 3021, Level = LogLevel.Warning,
        Message = "Repo confirm returned an unlisted repository ({Repo}); parking for triage")]
    private partial void LogUnlistedRepo(string repo);

    [LoggerMessage(EventId = 3022, Level = LogLevel.Warning,
        Message = "Repo resolution failed (reason={Reason}); parking for triage")]
    private partial void LogResolveFailed(string reason);
}
