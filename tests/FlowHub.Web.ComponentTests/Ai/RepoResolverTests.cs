using System.Text.Json;
using FlowHub.AI;
using FlowHub.Core.Classification;
using FlowHub.Core.Skills;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;

namespace FlowHub.Web.ComponentTests.Ai;

public sealed class RepoResolverTests
{
    private readonly IChatClient _chat = Substitute.For<IChatClient>();
    private readonly IBridgeCatalog _catalog = Substitute.For<IBridgeCatalog>();
    private readonly IRepoEmbeddingStore _store = Substitute.For<IRepoEmbeddingStore>();
    private readonly IEmbeddingService _embeddings = Substitute.For<IEmbeddingService>();

    public RepoResolverTests()
    {
        _catalog.GetReposAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new BridgeRepo("game-nibbles", null, "Faithful browser Nibbles/Snake clone", [], null),
            new BridgeRepo("flowhub", null, "Capture anything.", [], null),
        ]);
        _store.GetHashesAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<string, string>());
        _embeddings.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[384]);
        _store.NearestAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(["game-nibbles", "flowhub"]);
    }

    private RepoResolver Sut() =>
        new(_chat, _catalog, _store, _embeddings,
            new ChatOptions { MaxOutputTokens = 300, Temperature = 0.2f },
            new RepoEmbeddingSynchronizer(_catalog, _store, _embeddings, NullLogger<RepoEmbeddingSynchronizer>.Instance),
            NullLogger<RepoResolver>.Instance);

    private void ChatReturns(object payload) =>
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(payload))));

    [Fact]
    public async Task ResolveAsync_ModelPicksAListedRepo_ReturnsIt()
    {
        ChatReturns(new { repo = "game-nibbles", action = "issue", title = "Snake too fast", body = "It speeds up." });

        var result = await Sut().ResolveAsync("the snake game is too fast", default);

        result!.Repo.Should().Be("game-nibbles");
        result.Action.Should().Be(BridgeAction.Issue);
        result.Title.Should().Be("Snake too fast");
    }

    [Fact]
    public async Task ResolveAsync_ModelAbstains_RoutesToIdeasLabAsIdea()
    {
        ChatReturns(new { repo = (string?)null, action = "idea", title = "Minigolf game", body = "Browser minigolf." });

        var result = await Sut().ResolveAsync("Game browser Minigolf", default);

        result!.Repo.Should().Be("ideas-lab");
        result.Action.Should().Be(BridgeAction.Idea);
    }

    [Fact]
    public async Task ResolveAsync_ModelNamesAnUnlistedRepo_ReturnsNull()
    {
        // The catalogue is authoritative: a name outside the shortlist is a schema
        // violation, which is what makes a hallucinated repo structurally impossible.
        ChatReturns(new { repo = "some-other-repo", action = "issue", title = "x", body = "y" });

        var result = await Sut().ResolveAsync("anything", default);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_EmbeddingsUnavailable_StillResolvesViaLexicalShortlist()
    {
        _embeddings.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((float[]?)null);
        ChatReturns(new { repo = "game-nibbles", action = "issue", title = "t", body = "b" });

        var result = await Sut().ResolveAsync("nibbles snake clone is broken", default);

        result!.Repo.Should().Be("game-nibbles");
    }

    [Fact]
    public async Task ResolveAsync_ConfirmCallThrows_ReturnsNullWithoutThrowing()
    {
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("model down"));

        var result = await Sut().ResolveAsync("anything", default);

        result.Should().BeNull();
    }
}
