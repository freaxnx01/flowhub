using FlowHub.Core.Skills;
using FlowHub.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace FlowHub.Persistence.Repositories;

internal sealed class EfRepoEmbeddingStore : IRepoEmbeddingStore
{
    private readonly FlowHubDbContext _db;

    public EfRepoEmbeddingStore(FlowHubDbContext db) => _db = db;

    public async Task<IReadOnlyDictionary<string, string>> GetHashesAsync(CancellationToken cancellationToken) =>
        await _db.RepoEmbeddings
            .AsNoTracking()
            .ToDictionaryAsync(r => r.RepoName, r => r.ContentHash, cancellationToken);

    public async Task UpsertAsync(
        string repoName, string contentHash, float[] embedding, CancellationToken cancellationToken)
    {
        var vector = new Vector(embedding);
        var existing = await _db.RepoEmbeddings
            .FirstOrDefaultAsync(r => r.RepoName == repoName, cancellationToken);

        if (existing is null)
        {
            _db.RepoEmbeddings.Add(new RepoEmbeddingEntity
            {
                RepoName = repoName,
                ContentHash = contentHash,
                Embedding = vector,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.ContentHash = contentHash;
            existing.Embedding = vector;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveMissingAsync(
        IReadOnlyCollection<string> keepRepoNames, CancellationToken cancellationToken)
    {
        var keep = RepoEmbeddingSql.ToOrdinalSet(keepRepoNames);
        await _db.RepoEmbeddings
            .Where(r => !keep.Contains(r.RepoName))
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> NearestAsync(
        float[] queryEmbedding, int limit, CancellationToken cancellationToken)
    {
        var safeLimit = Math.Clamp(limit, 1, 50);
        var vectorLiteral = RepoEmbeddingSql.ToVectorLiteral(queryEmbedding);

        var rows = await _db.RepoEmbeddings
            .FromSqlInterpolated($"""
                SELECT * FROM "RepoEmbeddings"
                WHERE "Embedding" IS NOT NULL
                ORDER BY "Embedding" <=> {vectorLiteral}::vector
                LIMIT {safeLimit}
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return rows.Select(r => r.RepoName).ToList();
    }
}
