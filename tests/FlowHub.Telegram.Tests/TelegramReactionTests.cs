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
    public void EmojiFor_TerminalStages_MapToAllowListedEmoji(LifecycleStage stage, string expected)
    {
        TelegramReactionService.EmojiFor(stage).Should().Be(expected);
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

        await sut.ApplyAsync(captureId, LifecycleStage.Completed, CancellationToken.None);

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

        var act = async () => await sut.ApplyAsync(Guid.NewGuid(), LifecycleStage.Completed, CancellationToken.None);

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

        var act = async () => await sut.ApplyAsync(captureId, LifecycleStage.Completed, CancellationToken.None);

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
