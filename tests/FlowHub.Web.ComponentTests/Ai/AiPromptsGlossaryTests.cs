using FlowHub.AI;

namespace FlowHub.Web.ComponentTests.Ai;

public sealed class AiPromptsGlossaryTests
{
    private static readonly string[] DefaultBuckets = ["Inbox", "Movies"];

    [Fact]
    public void BuildSystemPrompt_NoGlossary_IsByteIdenticalToTheCurrentPrompt()
    {
        // The guard that matters: an unconfigured deployment must classify exactly as
        // it does today. Prompt drift reclassifies every capture.
        var without = AiPrompts.BuildSystemPrompt(DefaultBuckets, allowBridge: true);
        var withEmpty = AiPrompts.BuildSystemPrompt(DefaultBuckets, allowBridge: true, glossaryContext: "");

        withEmpty.Should().Be(without);
    }

    [Fact]
    public void BuildSystemPrompt_WhitespaceGlossary_IsAlsoByteIdentical()
    {
        AiPrompts.BuildSystemPrompt(DefaultBuckets, allowBridge: true, glossaryContext: "   ")
            .Should().Be(AiPrompts.BuildSystemPrompt(DefaultBuckets, allowBridge: true));
    }

    [Fact]
    public void BuildSystemPrompt_WithGlossary_IncludesTheContext()
    {
        var prompt = AiPrompts.BuildSystemPrompt(
            DefaultBuckets, allowBridge: true, glossaryContext: "\"ZZZ\" means \"Zed Zed Zed\".");

        prompt.Should().Contain("Zed Zed Zed");
    }
}
