using FlowHub.Core.Skills;
using Microsoft.EntityFrameworkCore;

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
        // A read-then-write here is racy on the primary key: overlapping catalogue syncs
        // (RepoResolver calls SyncAsync per classification, and the pipeline consumers run
        // concurrently) would both see no row, both INSERT, and one would fail on the PK
        // instead of overwriting. ON CONFLICT makes last-writer-wins the actual behaviour
        // rather than the intended one.
        var vectorLiteral = RepoEmbeddingSql.ToVectorLiteral(embedding);
        var updatedAt = DateTimeOffset.UtcNow;

        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "RepoEmbeddings" ("RepoName", "ContentHash", "Embedding", "UpdatedAt")
            VALUES ({repoName}, {contentHash}, {vectorLiteral}::vector, {updatedAt})
            ON CONFLICT ("RepoName") DO UPDATE SET
                "ContentHash" = EXCLUDED."ContentHash",
                "Embedding"   = EXCLUDED."Embedding",
                "UpdatedAt"   = EXCLUDED."UpdatedAt"
            """, cancellationToken);
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
