using System.Text.Json;
using FlowHub.AI;
using FlowHub.Core.Classification;
using FlowHub.Core.Skills;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowHub.Web.ComponentTests.Ai;

public sealed class AiClassifierGlossaryTests
{
    private readonly IChatClient _chat = Substitute.For<IChatClient>();
    private readonly IClassifier _keyword = Substitute.For<IClassifier>();
    private readonly ChatOptions _opts = new() { MaxOutputTokens = 300, Temperature = 0.2f };
    private readonly IVikunjaProjectCatalog _vikunja = Substitute.For<IVikunjaProjectCatalog>();
    private readonly IBridgeCatalog _bridge = Substitute.For<IBridgeCatalog>();

    public AiClassifierGlossaryTests()
    {
        _vikunja.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, int> { ["Inbox"] = 1 });
        _bridge.GetAliasesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal)));
    }

    private static IGlossary GlossaryOf(GlossarySnapshot snapshot)
    {
        var glossary = Substitute.For<IGlossary>();
        glossary.GetAsync(Arg.Any<CancellationToken>()).Returns(snapshot);
        return glossary;
    }

    private static GlossarySnapshot WithRepoPrefix() => new(
        People: new Dictionary<string, string> { ["aa"] = "Person A" },
        Acronyms: new Dictionary<string, string> { ["ZZZ"] = "Zed Zed Zed" },
        Prefixes: new Dictionary<string, string> { ["Repo Thing:"] = "someone/some-repo" });

    private AiClassifier Sut(IGlossary glossary, bool allowBridge) =>
        new(_chat, _keyword, NullLogger<AiClassifier>.Instance, _opts, _vikunja,
            new AiModelInfo("OpenRouter", "test-model"), _bridge,
            allowBridgeClassification: allowBridge, repoResolver: null, glossary: glossary);

    private static ChatResponse JsonResponse(object payload) =>
        new(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(payload)));

    private void ModelReturns(object payload) =>
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(JsonResponse(payload));

    [Fact]
    public async Task ClassifyAsync_OwnerQualifiedPrefix_RoutesToBridgeWithoutCallingTheModel()
    {
        var result = await Sut(GlossaryOf(WithRepoPrefix()), allowBridge: true)
            .ClassifyAsync("Repo Thing: SPQR", default);

        result.MatchedSkill.Should().Be("Bridge");
        result.BridgeTarget.Should().Be("someone/some-repo");

        await _chat.DidNotReceive().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClassifyAsync_OwnerQualifiedPrefix_BridgeDisabled_DoesNotRouteToBridge()
    {
        // The Bridge skill is behind a feature flag. An owner-qualified glossary prefix
        // must not become a way around it on a deployment with no bridge wired up.
        ModelReturns(new { tags = new[] { "t" }, matched_skill = "Vikunja", title = "t", project = "Inbox", entities = (object?)null });

        var result = await Sut(GlossaryOf(WithRepoPrefix()), allowBridge: false)
            .ClassifyAsync("Repo Thing: SPQR", default);

        result.MatchedSkill.Should().NotBe("Bridge");
        result.BridgeTarget.Should().BeNull();
    }

    [Fact]
    public async Task ClassifyAsync_ResolvedEntities_AreMergedIntoTheResult()
    {
        ModelReturns(new { tags = new[] { "t" }, matched_skill = "Vikunja", title = "t", project = "Inbox", entities = (object?)null });

        var glossary = GlossaryOf(new GlossarySnapshot(
            People: new Dictionary<string, string> { ["aa"] = "Person A" },
            Acronyms: new Dictionary<string, string>(),
            Prefixes: new Dictionary<string, string>()));

        var result = await Sut(glossary, allowBridge: false).ClassifyAsync("Coffee für aa", default);

        result.Entities.Should().NotBeNull();
        result.Entities!["person"].Should().Be("Person A");
    }

    [Fact]
    public async Task ClassifyAsync_ModelEntities_WinOverResolvedOnes()
    {
        ModelReturns(new
        {
            tags = new[] { "t" },
            matched_skill = "Vikunja",
            title = "t",
            project = "Inbox",
            entities = new Dictionary<string, string> { ["person"] = "From The Model" },
        });

        var glossary = GlossaryOf(new GlossarySnapshot(
            People: new Dictionary<string, string> { ["aa"] = "Person A" },
            Acronyms: new Dictionary<string, string>(),
            Prefixes: new Dictionary<string, string>()));

        var result = await Sut(glossary, allowBridge: false).ClassifyAsync("Coffee für aa", default);

        result.Entities!["person"].Should().Be("From The Model");
    }

    [Fact]
    public async Task ClassifyAsync_UnknownTrailingToken_IsSurfacedOnTheResult()
    {
        ModelReturns(new { tags = new[] { "t" }, matched_skill = "Vikunja", title = "t", project = "Inbox", entities = (object?)null });

        var glossary = GlossaryOf(new GlossarySnapshot(
            People: new Dictionary<string, string> { ["aa"] = "Person A" },
            Acronyms: new Dictionary<string, string>(),
            Prefixes: new Dictionary<string, string>()));

        var result = await Sut(glossary, allowBridge: false).ClassifyAsync("Farmshop qq", default);

        result.UnknownShorthand.Should().ContainSingle().Which.Should().Be("qq");
    }

    [Fact]
    public async Task ClassifyAsync_EmptyGlossary_LeavesEntitiesAndUnknownsAlone()
    {
        ModelReturns(new { tags = new[] { "t" }, matched_skill = "Vikunja", title = "t", project = "Inbox", entities = (object?)null });

        var result = await Sut(GlossaryOf(GlossarySnapshot.Empty), allowBridge: false)
            .ClassifyAsync("Farmshop qq", default);

        result.Entities.Should().BeNull();
        result.UnknownShorthand.Should().BeNull();
    }
}
