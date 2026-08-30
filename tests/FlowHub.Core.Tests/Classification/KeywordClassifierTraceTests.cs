using FlowHub.Core.Classification;
using FluentAssertions;

namespace FlowHub.Core.Tests.Classification;

public sealed class KeywordClassifierTraceTests
{
    private sealed class NoBridgeAliases : FlowHub.Core.Skills.IBridgeCatalog
    {
        public Task<IReadOnlySet<string>> GetAliasesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
        public Task<IReadOnlyList<FlowHub.Core.Skills.BridgeRepo>> GetReposAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FlowHub.Core.Skills.BridgeRepo>>(Array.Empty<FlowHub.Core.Skills.BridgeRepo>());
    }

    [Fact]
    public async Task ClassifyAsync_SetsKeywordTrace_WithNoTokens()
    {
        var sut = new KeywordClassifier(new NoBridgeAliases());

        var result = await sut.ClassifyAsync("https://example.com", default);

        result.Trace.Should().NotBeNull();
        result.Trace!.Kind.Should().Be(ClassifierKind.Keyword);
        result.Trace.LatencyMs.Should().BeGreaterThanOrEqualTo(0);
        result.Trace.PromptTokens.Should().BeNull();
        result.Trace.CompletionTokens.Should().BeNull();
        result.Trace.Provider.Should().BeNull();
        result.Trace.Model.Should().BeNull();
    }

    [Fact]
    public async Task ClassifyAsync_TodoAndUnsorted_AlsoCarryKeywordTrace()
    {
        var sut = new KeywordClassifier(new NoBridgeAliases());
        (await sut.ClassifyAsync("todo: buy milk", default)).Trace!.Kind.Should().Be(ClassifierKind.Keyword);
        (await sut.ClassifyAsync("random musings", default)).Trace!.Kind.Should().Be(ClassifierKind.Keyword);
    }
}
