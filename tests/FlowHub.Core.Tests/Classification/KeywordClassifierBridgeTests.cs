using FlowHub.Core.Classification;
using FlowHub.Core.Skills;
using FluentAssertions;

namespace FlowHub.Core.Tests.Classification;

public sealed class KeywordClassifierBridgeTests
{
    private sealed class StubBridgeCatalog(params string[] aliases) : IBridgeCatalog
    {
        private readonly IReadOnlySet<string> _aliases = new HashSet<string>(aliases, StringComparer.Ordinal);
        public Task<IReadOnlySet<string>> GetAliasesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_aliases);
        public Task<IReadOnlyList<BridgeRepo>> GetReposAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BridgeRepo>>(Array.Empty<BridgeRepo>());
    }

    [Fact]
    public async Task ClassifyAsync_LeadingAlias_RoutesToBridgeWithAliasAndUnknownAction()
    {
        var sut = new KeywordClassifier(new StubBridgeCatalog("br"));

        var result = await sut.ClassifyAsync("br the login 500s on Safari", default);

        result.MatchedSkill.Should().Be("Bridge");
        result.BridgeAlias.Should().Be("br");
        result.BridgeAction.Should().Be(BridgeAction.Unknown);
        result.Tags.Should().ContainSingle().Which.Should().Be("bridge");
    }

    [Fact]
    public async Task ClassifyAsync_AliasTakesPrecedenceOverUrlAndTodo()
    {
        var sut = new KeywordClassifier(new StubBridgeCatalog("br"));

        // Body contains a url + "todo" but the leading alias wins.
        var result = await sut.ClassifyAsync("br todo read https://example.com", default);

        result.MatchedSkill.Should().Be("Bridge");
        result.BridgeAlias.Should().Be("br");
    }

    [Fact]
    public async Task ClassifyAsync_NoAliasMatch_FallsThroughToExistingRules()
    {
        var sut = new KeywordClassifier(new StubBridgeCatalog("br"));

        var url = await sut.ClassifyAsync("https://example.com", default);
        var todo = await sut.ClassifyAsync("todo: buy milk", default);

        url.MatchedSkill.Should().Be("Wallabag");
        todo.MatchedSkill.Should().Be("Vikunja");
    }

    [Fact]
    public async Task ClassifyAsync_EmptyCatalog_BehavesAsBefore()
    {
        var sut = new KeywordClassifier(new StubBridgeCatalog());

        var result = await sut.ClassifyAsync("br the login 500s", default);

        // "br the login 500s" is not a url and has no todo/task keyword → Orphan.
        result.MatchedSkill.Should().BeEmpty();
        result.BridgeAlias.Should().BeNull();
    }
}
