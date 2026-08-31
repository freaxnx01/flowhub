using System.Text.Json;
using FlowHub.AI;
using FlowHub.Core.Classification;
using FlowHub.Core.Skills;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowHub.Web.ComponentTests.Ai;

public sealed class AiClassifierRepoInferenceTests
{
    private readonly IChatClient _chat = Substitute.For<IChatClient>();
    private readonly IClassifier _keyword = Substitute.For<IClassifier>();
    private readonly ChatOptions _opts = new() { MaxOutputTokens = 300, Temperature = 0.2f };
    private readonly IVikunjaProjectCatalog _vikunja = Substitute.For<IVikunjaProjectCatalog>();
    private readonly IBridgeCatalog _bridge = Substitute.For<IBridgeCatalog>();
    private readonly IRepoEmbeddingStore _store = Substitute.For<IRepoEmbeddingStore>();
    private readonly IEmbeddingService _embeddings = Substitute.For<IEmbeddingService>();

    public AiClassifierRepoInferenceTests()
    {
        _vikunja.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, int> { ["Inbox"] = 1 });
        _bridge.GetAliasesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<string>>(
                new HashSet<string>(StringComparer.Ordinal)));
        _bridge.GetReposAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new BridgeRepo("game-nibbles", null, "Faithful browser Nibbles/Snake clone", [], null),
        ]);
        _store.GetHashesAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<string, string>());
        _embeddings.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[384]);
        _store.NearestAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(["game-nibbles"]);
    }

    private AiClassifier SutWithResolver()
    {
        var sync = new RepoEmbeddingSynchronizer(_bridge, _store, _embeddings, NullLogger<RepoEmbeddingSynchronizer>.Instance);
        var resolver = new RepoResolver(
            _chat, _bridge, _store, _embeddings, _opts, sync, NullLogger<RepoResolver>.Instance);
        return new AiClassifier(
            _chat, _keyword, NullLogger<AiClassifier>.Instance, _opts, _vikunja,
            new AiModelInfo("OpenRouter", "test-model"), _bridge,
            allowBridgeClassification: true, repoResolver: resolver);
    }

    private static ChatResponse JsonResponse(object payload) =>
        new(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(payload)));

    [Fact]
    public async Task ClassifyAsync_BridgeWithoutAlias_ResolverSuppliesTheRepo()
    {
        // First call: classification returns Bridge. Second: the confirm call.
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                JsonResponse(new { tags = new[] { "dev" }, matched_skill = "Bridge", title = "t", project = (string?)null, entities = (object?)null }),
                JsonResponse(new { repo = "game-nibbles", action = "issue", title = "Snake too fast", body = "It speeds up." }));

        var result = await SutWithResolver().ClassifyAsync("the snake game is too fast", default);

        result.MatchedSkill.Should().Be("Bridge");
        result.BridgeAlias.Should().Be("game-nibbles");
        result.BridgeAction.Should().Be(BridgeAction.Issue);
    }

    [Fact]
    public async Task ClassifyAsync_ResolverReturnsNull_LeavesAliasNullForParking()
    {
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                JsonResponse(new { tags = new[] { "dev" }, matched_skill = "Bridge", title = "t", project = (string?)null, entities = (object?)null }),
                JsonResponse(new { repo = "not-in-shortlist", action = "issue", title = "t", body = "b" }));

        var result = await SutWithResolver().ClassifyAsync("something", default);

        result.MatchedSkill.Should().Be("Bridge");
        result.BridgeAlias.Should().BeNull();
        result.BridgeAction.Should().Be(BridgeAction.Unknown);
    }
}
