using FlowHub.Persistence.Repositories;
using FlowHub.Persistence.Tests.Fixtures;

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
}
