using FlowHub.Core.Classification;

namespace FlowHub.Web.ComponentTests.Ai;

public sealed class AiClassifierGlossaryTests
{
    [Fact]
    public void ClassificationResult_CarriesUnknownShorthand()
    {
        var result = new ClassificationResult(
            ["t"], "Vikunja", UnknownShorthand: ["qq"]);

        result.UnknownShorthand.Should().ContainSingle().Which.Should().Be("qq");
    }

    [Fact]
    public void ClassificationResult_DefaultsUnknownShorthandToNull()
    {
        new ClassificationResult(["t"], "Vikunja").UnknownShorthand.Should().BeNull();
    }
}
