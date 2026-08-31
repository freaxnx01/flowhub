using FlowHub.AI;
using FlowHub.Core.Captures;
using FlowHub.Core.Skills;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowHub.Web.ComponentTests.Ai;

public sealed class RepoEmbeddingSynchronizerTests
{
    private readonly IBridgeCatalog _catalog = Substitute.For<IBridgeCatalog>();
    private readonly IRepoEmbeddingStore _store = Substitute.For<IRepoEmbeddingStore>();
    private readonly IEmbeddingService _embeddings = Substitute.For<IEmbeddingService>();

    private RepoEmbeddingSynchronizer Sut() =>
        new(_catalog, _store, _embeddings, NullLogger<RepoEmbeddingSynchronizer>.Instance);

    private static BridgeRepo Repo(string name, string? desc = null) =>
        new(name, null, desc, [], null);

    [Fact]
    public async Task SyncAsync_NewRepo_EmbedsAndUpserts()
    {
        _catalog.GetReposAsync(Arg.Any<CancellationToken>())
            .Returns([Repo("flowhub", "Capture anything.")]);
        _store.GetHashesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>());
        _embeddings.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[384]);

        await Sut().SyncAsync(default);

        await _store.Received(1).UpsertAsync("flowhub", Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_UnchangedRepo_MakesNoEmbeddingCall()
    {
        var repo = Repo("flowhub", "Capture anything.");
        _catalog.GetReposAsync(Arg.Any<CancellationToken>()).Returns([repo]);
        _store.GetHashesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["flowhub"] = RepoEmbeddingSynchronizer.HashOf(repo) });

        await Sut().SyncAsync(default);

        await _embeddings.DidNotReceive().GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().UpsertAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_ChangedDescription_ReEmbeds()
    {
        _catalog.GetReposAsync(Arg.Any<CancellationToken>())
            .Returns([Repo("flowhub", "New description.")]);
        _store.GetHashesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["flowhub"] = "stale-hash" });
        _embeddings.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[384]);

        await Sut().SyncAsync(default);

        await _embeddings.Received(1).GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_Always_PrunesReposNoLongerInTheCatalogue()
    {
        _catalog.GetReposAsync(Arg.Any<CancellationToken>()).Returns([Repo("kept")]);
        _store.GetHashesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["kept"] = "h", ["gone"] = "h" });

        await Sut().SyncAsync(default);

        await _store.Received(1).RemoveMissingAsync(
            Arg.Is<IReadOnlyCollection<string>>(k => k.Contains("kept") && !k.Contains("gone")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_EmbeddingServiceReturnsNull_SkipsWithoutThrowing()
    {
        _catalog.GetReposAsync(Arg.Any<CancellationToken>()).Returns([Repo("flowhub", "x")]);
        _store.GetHashesAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<string, string>());
        _embeddings.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((float[]?)null);

        var act = async () => await Sut().SyncAsync(default);

        await act.Should().NotThrowAsync();
        await _store.DidNotReceive().UpsertAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<CancellationToken>());
    }
}
