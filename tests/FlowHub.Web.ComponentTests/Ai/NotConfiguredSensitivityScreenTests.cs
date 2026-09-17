using FlowHub.AI;
using FlowHub.Core.Classification;

namespace FlowHub.Web.ComponentTests.Ai;

public sealed class NotConfiguredSensitivityScreenTests
{
    [Fact]
    public async Task ScreenAsync_AlwaysParks()
    {
        var result = await new NotConfiguredSensitivityScreen()
            .ScreenAsync("a completely ordinary errand", default);

        // With no model there is no way to judge sensitivity. Parking everything is
        // loudly wrong; routing everything is silently wrong — and is issue #93.
        result.Verdict.Should().Be(Sensitivity.Unsure);
        result.Reason.Should().Contain("not configured");
    }
}
