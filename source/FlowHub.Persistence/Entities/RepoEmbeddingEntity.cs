using Pgvector;

namespace FlowHub.Persistence.Entities;

internal sealed class RepoEmbeddingEntity
{
    public required string RepoName { get; set; }
    public required string ContentHash { get; set; }
    public Vector? Embedding { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
