using FlowHub.AI;
using FlowHub.Core.Classification;

namespace FlowHub.Web.ComponentTests.Ai;

public sealed class EmptyGlossaryTests
{
    [Fact]
    public async Task GetAsync_ReturnsAnEmptySnapshot()
    {
        var snapshot = await new EmptyGlossary().GetAsync(default);

        snapshot.IsEmpty.Should().BeTrue();
        snapshot.People.Should().BeEmpty();
        snapshot.Acronyms.Should().BeEmpty();
        snapshot.Prefixes.Should().BeEmpty();
    }
}
