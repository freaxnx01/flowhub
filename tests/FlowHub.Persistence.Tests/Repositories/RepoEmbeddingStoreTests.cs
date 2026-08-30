using FlowHub.Persistence.Entities;
using FlowHub.Persistence.Repositories;
using FlowHub.Persistence.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace FlowHub.Persistence.Tests.Repositories;

[Collection(PostgresGroup.Name)]
public sealed class RepoEmbeddingStoreTests(PostgresFixture fixture)
{
    private static float[] Vec(float first)
    {
        var v = new float[384];
        v[0] = first;
        v[1] = 1f;
        return v;
    }

    [Fact]
    public async Task UpsertAsync_ThenGetHashesAsync_ReturnsTheStoredHash()
    {
        await using var db = await fixture.CreateFreshDbAsync();
        var sut = new EfRepoEmbeddingStore(db);

        await sut.UpsertAsync("flowhub", "hash-1", Vec(1f), default);

        var hashes = await sut.GetHashesAsync(default);
        hashes["flowhub"].Should().Be("hash-1");
    }

    [Fact]
    public async Task UpsertAsync_SameRepoTwice_OverwritesRatherThanDuplicating()
    {
        await using var db = await fixture.CreateFreshDbAsync();
        var sut = new EfRepoEmbeddingStore(db);

        await sut.UpsertAsync("dup", "hash-1", Vec(1f), default);
        await sut.UpsertAsync("dup", "hash-2", Vec(2f), default);

        var hashes = await sut.GetHashesAsync(default);
        hashes["dup"].Should().Be("hash-2");
    }

    [Fact]
    public async Task RemoveMissingAsync_DropsRowsNotInTheKeepSet()
    {
        await using var db = await fixture.CreateFreshDbAsync();
        var sut = new EfRepoEmbeddingStore(db);
        await sut.UpsertAsync("keep", "h", Vec(1f), default);
        await sut.UpsertAsync("drop", "h", Vec(1f), default);

        await sut.RemoveMissingAsync(["keep"], default);

        (await sut.GetHashesAsync(default)).Keys.Should().NotContain("drop");
    }

    [Fact]
    public async Task NearestAsync_OrdersByCosineDistance()
    {
        await using var db = await fixture.CreateFreshDbAsync();
        var sut = new EfRepoEmbeddingStore(db);
        await sut.UpsertAsync("near", "h", Vec(10f), default);
        await sut.UpsertAsync("far", "h", Vec(-10f), default);

        var hits = await sut.NearestAsync(Vec(10f), 2, default);

        hits[0].Should().Be("near");
    }

    [Fact]
    public async Task NearestAsync_RowWithoutAnEmbedding_IsExcluded()
    {
        // The Embedding column is nullable, so a row can exist with a hash but no vector.
        // NearestAsync filters those out; without the filter pgvector would order them
        // arbitrarily and a repo with no embedding could win the shortlist.
        await using var db = await fixture.CreateFreshDbAsync();
        var sut = new EfRepoEmbeddingStore(db);
        await sut.UpsertAsync("embedded", "h", Vec(10f), default);
        db.RepoEmbeddings.Add(new RepoEmbeddingEntity
        {
            RepoName = "pending",
            ContentHash = "h",
            Embedding = null,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var hits = await sut.NearestAsync(Vec(10f), 50, default);

        hits.Should().ContainSingle().Which.Should().Be("embedded");
    }

    [Fact]
    public async Task UpsertAsync_ConcurrentCallsForTheSameRepo_AllSucceed()
    {
        // Overlapping catalogue syncs are reachable: RepoResolver syncs per classification
        // and the pipeline consumers run concurrently. A read-then-write would have both
        // callers INSERT the same primary key and one would fail.
        await using var db = await fixture.CreateFreshDbAsync();
        var connectionString = db.Database.GetConnectionString()!;

        FlowHubDbContext Connect() => new(
            new DbContextOptionsBuilder<FlowHubDbContext>()
                .UseNpgsql(connectionString, npgsql => npgsql.UseVector())
                .Options);

        var writes = Enumerable.Range(0, 8).Select(async i =>
        {
            // A DbContext is not thread-safe, so each concurrent writer needs its own.
            await using var scoped = Connect();
            await new EfRepoEmbeddingStore(scoped).UpsertAsync("racy", $"hash-{i}", Vec(i), default);
        });

        var act = async () => await Task.WhenAll(writes);

        await act.Should().NotThrowAsync();

        var hashes = await new EfRepoEmbeddingStore(db).GetHashesAsync(default);
        hashes.Should().ContainKey("racy");
    }
}
