using FlowHub.Core.Captures;
using FlowHub.Core.Classification;
using FlowHub.Core.Events;
using FluentAssertions;

namespace FlowHub.Core.Tests.Classification;

public sealed class BridgeActionDefaultsTests
{
    [Fact]
    public void BridgeAction_Default_IsUnknown()
    {
        default(BridgeAction).Should().Be(BridgeAction.Unknown);
    }

    [Fact]
    public void ClassificationResult_WithoutBridgeFields_DefaultsToUnknownAndNull()
    {
        var result = new ClassificationResult(["unsorted"], "");

        result.BridgeAlias.Should().BeNull();
        result.BridgeAction.Should().Be(BridgeAction.Unknown);
        result.BridgeBody.Should().BeNull();
    }

    [Fact]
    public void ClassificationResult_WithBridgeFields_CarriesThem()
    {
        var result = new ClassificationResult(
            ["bridge"], "Bridge",
            Title: "Login 500 on Safari",
            BridgeAlias: "br",
            BridgeAction: BridgeAction.Issue,
            BridgeBody: "The login endpoint returns 500…");

        result.MatchedSkill.Should().Be("Bridge");
        result.BridgeAlias.Should().Be("br");
        result.BridgeAction.Should().Be(BridgeAction.Issue);
        result.BridgeBody.Should().Be("The login endpoint returns 500…");
    }

    [Fact]
    public void CaptureClassified_CarriesBridgeFields()
    {
        var evt = new CaptureClassified(
            Guid.NewGuid(), ["bridge"], "Bridge", DateTimeOffset.UtcNow,
            BridgeAlias: "agp", BridgeAction: BridgeAction.Idea, BridgeBody: "what if repos had a health score");

        evt.BridgeAlias.Should().Be("agp");
        evt.BridgeAction.Should().Be(BridgeAction.Idea);
        evt.BridgeBody.Should().Be("what if repos had a health score");
    }

    [Fact]
    public void Capture_CarriesBridgeFieldsViaWith()
    {
        var capture = new Capture(
            Guid.NewGuid(), ChannelKind.Web, "br fix the thing",
            DateTimeOffset.UtcNow, LifecycleStage.Classified, "Bridge");

        var grafted = capture with { BridgeAlias = "br", BridgeAction = BridgeAction.Issue, BridgeBody = "fix the thing" };

        grafted.BridgeAlias.Should().Be("br");
        grafted.BridgeAction.Should().Be(BridgeAction.Issue);
        grafted.BridgeBody.Should().Be("fix the thing");
    }
}
