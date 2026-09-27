using FlowHub.Core.Captures;

namespace FlowHub.Telegram.Tests;

public class TelegramLegendTests
{
    public static TheoryData<string> ReactionEmojis()
    {
        var emojis = new TheoryData<string> { TelegramReactionService.InFlightEmoji };
        foreach (var stage in Enum.GetValues<LifecycleStage>())
        {
            if (TelegramReactionService.EmojiFor(stage) is { } emoji)
            {
                emojis.Add(emoji);
            }
        }

        foreach (var skill in TelegramReactionService.KnownSkills)
        {
            emojis.Add(TelegramReactionService.EmojiFor(LifecycleStage.Completed, skill)!);
        }

        return emojis;
    }

    [Theory]
    [MemberData(nameof(ReactionEmojis))]
    public void Legend_EveryEmojiTheBotCanReactWith_IsExplained(string emoji)
    {
        // Built from the same mapping the reactions use, so a stage or skill added
        // without a legend line turns this red rather than leaving an emoji unexplained.
        TelegramReactionService.Legend.Should().Contain(line => line.StartsWith(emoji + " "));
    }
}
