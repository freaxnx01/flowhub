using FlowHub.Core.Captures;
using FlowHub.Core.Channels;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowHub.Telegram.Tests;

public class TelegramReactionTests
{
    [Theory]
    [InlineData(LifecycleStage.Completed, "👍")]
    [InlineData(LifecycleStage.Orphan, "💔")]
    [InlineData(LifecycleStage.Unhandled, "🤔")]
    [InlineData(LifecycleStage.Withheld, "🙊")]
    public void EmojiFor_TerminalStages_MapToAllowListedEmoji(LifecycleStage stage, string expected)
    {
        TelegramReactionService.EmojiFor(stage).Should().Be(expected);
    }

    [Theory]
    [InlineData("Bridge", "👨‍💻")]
    [InlineData("Vikunja", "✍")]
    [InlineData("Wallabag", "👀")]
    [InlineData("Paperless", "👌")]
    [InlineData("vikunja", "✍")]
    public void EmojiFor_Completed_IsDeterminedByTheMatchedSkill(string skill, string expected)
    {
        // Telegram allows a bot one reaction per message, so the success emoji is the
        // only place the skill can be shown. A capture only has a skill once it
        // completed, so one reaction carries both facts.
        TelegramReactionService.EmojiFor(LifecycleStage.Completed, skill).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SomeSkillAddedLater")]
    public void EmojiFor_Completed_UnknownSkill_FallsBackToThumbsUp(string? skill)
    {
        // A new skill must never be silent: falling back to the old success emoji is
        // wrong-but-visible, where null would set no reaction at all.
        TelegramReactionService.EmojiFor(LifecycleStage.Completed, skill).Should().Be("👍");
    }

    [Fact]
    public void EveryEmojiTheServiceCanEmit_IsOnTheReactionTypeEmojiAllowList()
    {
        // Telegram rejects an off-list emoji at the API, and ApplyAsync swallows the
        // HttpRequestException by design — so a bad emoji is invisible until someone
        // reads the chat. This test is the only thing that catches it.
        string[] allowList =
        [
            "❤", "👍", "👎", "🔥", "🥰", "👏", "😁", "🤔", "🤯", "😱", "🤬", "😢", "🎉", "🤩",
            "🤮", "💩", "🙏", "👌", "🕊", "🤡", "🥱", "🥴", "😍", "🐳", "❤‍🔥", "🌚", "🌭", "💯",
            "🤣", "⚡", "🍌", "🏆", "💔", "🤨", "😐", "🍓", "🍾", "💋", "🖕", "😈", "😴", "😭",
            "🤓", "👻", "👨‍💻", "👀", "🎃", "🙈", "😇", "😨", "🤝", "✍", "🤗", "🫡", "🎅", "🎄",
            "☃", "💅", "🤪", "🗿", "🆒", "💘", "🙉", "🦄", "😘", "💊", "🙊", "😎", "👾",
            "🤷‍♂", "🤷", "🤷‍♀", "😡",
        ];
        string?[] skills = [null, "Bridge", "Vikunja", "Wallabag", "Paperless", "Unknown"];

        var emitted = Enum.GetValues<LifecycleStage>()
            .SelectMany(stage => skills.Select(skill => TelegramReactionService.EmojiFor(stage, skill)))
            .Append(TelegramReactionService.InFlightEmoji)
            .Where(e => e is not null)
            .Distinct()
            .ToList();

        emitted.Should().NotBeEmpty();
        emitted.Should().OnlyContain(e => allowList.Contains(e));
    }

    [Fact]
    public void EmojiFor_Withheld_IsDistinctFromTheOtherTerminalStages()
    {
        // A withheld capture is terminal, non-retryable and otherwise silent. If it
        // shared an emoji with Orphan or Unhandled the operator could not tell a
        // deliberate privacy park from a failure.
        var withheld = TelegramReactionService.EmojiFor(LifecycleStage.Withheld);

        withheld.Should().NotBeNull();
        withheld.Should().NotBe(TelegramReactionService.EmojiFor(LifecycleStage.Orphan));
        withheld.Should().NotBe(TelegramReactionService.EmojiFor(LifecycleStage.Unhandled));
        withheld.Should().NotBe(TelegramReactionService.EmojiFor(LifecycleStage.Completed));
    }

    [Theory]
    [InlineData(LifecycleStage.Raw)]
    [InlineData(LifecycleStage.Classified)]
    [InlineData(LifecycleStage.Routed)]
    public void EmojiFor_NonTerminalStages_IsNull(LifecycleStage stage)
    {
        TelegramReactionService.EmojiFor(stage).Should().BeNull();
    }

    [Fact]
    public async Task ApplyAsync_KnownCapture_SetsTheReaction()
    {
        var repo = Substitute.For<ITelegramUpdateRepository>();
        var gateway = Substitute.For<ITelegramGateway>();
        var captureId = Guid.NewGuid();
        repo.FindByCaptureIdAsync(captureId, Arg.Any<CancellationToken>())
            .Returns(new TelegramUpdate(1L, 55L, 7, captureId, DateTimeOffset.UtcNow));
        var sut = new TelegramReactionService(repo, gateway, NullLogger<TelegramReactionService>.Instance);

        await sut.ApplyAsync(captureId, LifecycleStage.Completed, cancellationToken: CancellationToken.None);

        await gateway.Received(1).SetReactionAsync(55L, 7, "👍", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_NoMatchingUpdate_IsANoOp()
    {
        var repo = Substitute.For<ITelegramUpdateRepository>();
        repo.FindByCaptureIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((TelegramUpdate?)null);
        var gateway = Substitute.For<ITelegramGateway>();
        var sut = new TelegramReactionService(repo, gateway, NullLogger<TelegramReactionService>.Instance);

        var act = async () => await sut.ApplyAsync(Guid.NewGuid(), LifecycleStage.Completed, cancellationToken: CancellationToken.None);

        await act.Should().NotThrowAsync();
        await gateway.DidNotReceiveWithAnyArgs().SetReactionAsync(default, default, default!, default);
    }

    [Fact]
    public async Task ApplyAsync_GatewayThrows_DoesNotPropagate()
    {
        var repo = Substitute.For<ITelegramUpdateRepository>();
        var captureId = Guid.NewGuid();
        repo.FindByCaptureIdAsync(captureId, Arg.Any<CancellationToken>())
            .Returns(new TelegramUpdate(1L, 55L, 7, captureId, DateTimeOffset.UtcNow));
        var gateway = Substitute.For<ITelegramGateway>();
        gateway.SetReactionAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new HttpRequestException("telegram down"));
        var sut = new TelegramReactionService(repo, gateway, NullLogger<TelegramReactionService>.Instance);

        var act = async () => await sut.ApplyAsync(captureId, LifecycleStage.Completed, cancellationToken: CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Decorator_MarkCompletedAsync_CallsInnerThenReacts()
    {
        var inner = Substitute.For<ICaptureService>();
        var repo = Substitute.For<ITelegramUpdateRepository>();
        var gateway = Substitute.For<ITelegramGateway>();
        var captureId = Guid.NewGuid();
        repo.FindByCaptureIdAsync(captureId, Arg.Any<CancellationToken>())
            .Returns(new TelegramUpdate(1L, 55L, 7, captureId, DateTimeOffset.UtcNow));
        var reactions = new TelegramReactionService(repo, gateway, NullLogger<TelegramReactionService>.Instance);
        var sut = new TelegramReactionCaptureServiceDecorator(inner, reactions);

        await sut.MarkCompletedAsync(captureId, "wallabag-99", CancellationToken.None);

        await inner.Received(1).MarkCompletedAsync(captureId, "wallabag-99", Arg.Any<CancellationToken>());
        await gateway.Received(1).SetReactionAsync(55L, 7, "👍", Arg.Any<CancellationToken>());
    }
}
