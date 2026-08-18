using System.Text.Json;
using FlowHub.AI;
using FlowHub.Core.Classification;
using FlowHub.Core.Skills;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;

namespace FlowHub.Web.ComponentTests.Ai;

public sealed class AiClassifierBridgeTests
{
    private readonly IChatClient _chat = Substitute.For<IChatClient>();
    private readonly IClassifier _keyword = Substitute.For<IClassifier>();
    private readonly ChatOptions _opts = new() { MaxOutputTokens = 300, Temperature = 0.2f };
    private readonly IVikunjaProjectCatalog _catalog = Substitute.For<IVikunjaProjectCatalog>();
    private readonly IBridgeCatalog _bridge = Substitute.For<IBridgeCatalog>();

    public AiClassifierBridgeTests()
    {
        _catalog.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, int> { ["Inbox"] = 1 });
        _bridge.GetAliasesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<string>>(
                new HashSet<string>(StringComparer.Ordinal) { "br" }));
    }

    private AiClassifier Sut() =>
        new(_chat, _keyword, NullLogger<AiClassifier>.Instance, _opts, _catalog,
            new AiModelInfo("OpenRouter", "test-model"), _bridge);

    private static ChatResponse JsonResponse(object payload) =>
        new(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(payload)));

    [Fact]
    public async Task ClassifyAsync_BridgeAliasIssueWording_ReturnsBridgeIssueWithTitleAndBody()
    {
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(JsonResponse(new
            {
                action = "issue",
                title = "Login 500 on Safari",
                body = "The login endpoint intermittently returns 500 on Safari.",
                tags = new[] { "bug", "auth" },
            }));

        var result = await Sut().ClassifyAsync("br the login 500s on Safari sometimes", default);

        result.MatchedSkill.Should().Be("Bridge");
        result.BridgeAlias.Should().Be("br");
        result.BridgeAction.Should().Be(BridgeAction.Issue);
        result.Title.Should().Be("Login 500 on Safari");
        result.BridgeBody.Should().Be("The login endpoint intermittently returns 500 on Safari.");
        result.Tags.Should().BeEquivalentTo(new[] { "bug", "auth" });
        await _keyword.DidNotReceive().ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClassifyAsync_BridgeAliasIdeaWording_ReturnsBridgeIdea()
    {
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(JsonResponse(new
            {
                action = "idea",
                title = "Repo health score",
                body = "What if repos had a health score.",
                tags = new[] { "idea" },
            }));

        var result = await Sut().ClassifyAsync("br what if repos had a health score", default);

        result.MatchedSkill.Should().Be("Bridge");
        result.BridgeAction.Should().Be(BridgeAction.Idea);
        result.BridgeBody.Should().Be("What if repos had a health score.");
    }

    [Fact]
    public async Task ClassifyAsync_BridgeAliasUnsure_ReturnsBridgeUnknown()
    {
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(JsonResponse(new { action = "unknown", title = (string?)null, body = "ambiguous", tags = Array.Empty<string>() }));

        var result = await Sut().ClassifyAsync("br hmm", default);

        result.MatchedSkill.Should().Be("Bridge");
        result.BridgeAction.Should().Be(BridgeAction.Unknown);
        result.BridgeAlias.Should().Be("br");
    }

    [Fact]
    public async Task ClassifyAsync_NoAliasMatch_UsesGenericPathNotBridge()
    {
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(JsonResponse(new { tags = new[] { "link" }, matched_skill = "Wallabag", title = "An article", project = (string?)null, entities = (object?)null }));

        var result = await Sut().ClassifyAsync("https://example.com/article", default);

        result.MatchedSkill.Should().Be("Wallabag");
        result.BridgeAlias.Should().BeNull();
    }

    [Fact]
    public async Task ClassifyAsync_BridgeAliasButModelThrows_FallsBackToKeyword()
    {
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("boom"));
        _keyword.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ClassificationResult(["bridge"], "Bridge", BridgeAlias: "br"));

        var result = await Sut().ClassifyAsync("br the login 500s", default);

        await _keyword.Received(1).ClassifyAsync("br the login 500s", Arg.Any<CancellationToken>());
        result.MatchedSkill.Should().Be("Bridge");
        result.BridgeAction.Should().Be(BridgeAction.Unknown);
    }
}
