using FlowHub.AI;
using Microsoft.Extensions.AI;

namespace FlowHub.Web.ComponentTests.Ai;

public sealed class AiPromptsTests
{
    private static readonly string[] DefaultBuckets = ["Inbox", "Zitate"];

    [Fact]
    public void BuildMessages_AnyContent_FirstMessageIsSystemPrompt()
    {
        var messages = AiPrompts.BuildMessages("https://example.com", DefaultBuckets);

        messages.Should().HaveCount(2);
        messages[0].Role.Should().Be(ChatRole.System);
        messages[0].Text.Should().Contain("FlowHub");
        messages[0].Text.Should().Contain("Wallabag");
        messages[0].Text.Should().Contain("Vikunja");
    }

    [Fact]
    public void BuildMessages_AnyContent_SecondMessageIsRawUserContent()
    {
        const string content = "todo: buy milk on Saturday";

        var messages = AiPrompts.BuildMessages(content, DefaultBuckets);

        messages[1].Role.Should().Be(ChatRole.User);
        messages[1].Text.Should().Be(content);
    }

    [Fact]
    public void BuildSystemPrompt_HasNoGermanRoutingTokens()
    {
        // Spec D6 / Prompt strategy: the system prompt is English to keep Llama 3.1
        // routing tokens stable. Capture content can still be German — that's the user
        // message, not the system prompt.
        var prompt = AiPrompts.BuildSystemPrompt(DefaultBuckets);
        prompt.Should().NotContain("Ablage");
        prompt.Should().NotContain("Aufgabe");
    }

    [Fact]
    public void BuildSystemPrompt_BridgeDisabled_DoesNotOfferBridge()
    {
        var prompt = AiPrompts.BuildSystemPrompt(DefaultBuckets, allowBridge: false);

        prompt.Should().NotContain("Bridge");
    }

    [Fact]
    public void BuildSystemPrompt_BridgeDisabled_IsIdenticalToDefault()
    {
        // Prompt drift silently changes classification for every capture, not just
        // dev ones. The default and the explicitly-disabled prompt must not diverge.
        AiPrompts.BuildSystemPrompt(DefaultBuckets, allowBridge: false)
            .Should().Be(AiPrompts.BuildSystemPrompt(DefaultBuckets));
    }

    [Fact]
    public void BuildSystemPrompt_BridgeEnabled_OffersBridgeAndKeepsBuckets()
    {
        var prompt = AiPrompts.BuildSystemPrompt(DefaultBuckets, allowBridge: true);

        prompt.Should().Contain("\"Bridge\"");
        prompt.Should().Contain("Wallabag");
        prompt.Should().Contain("Vikunja");
        prompt.Should().Contain("Inbox, Zitate");
    }

    [Fact]
    public void BuildSystemPrompt_BridgeEnabled_DoesNotAskForARepositoryName()
    {
        // Repo inference is issue #38. The model has no catalogue here and would
        // hallucinate a name, so the prompt must not invite one.
        var prompt = AiPrompts.BuildSystemPrompt(DefaultBuckets, allowBridge: true);

        prompt.Should().NotContain("repository name");
        prompt.Should().NotContain("alias");
    }

    [Fact]
    public void BuildMessages_BridgeEnabled_SystemMessageOffersBridge()
    {
        var messages = AiPrompts.BuildMessages("fix the login bug", DefaultBuckets, allowBridge: true);

        messages[0].Text.Should().Contain("\"Bridge\"");
    }
}
