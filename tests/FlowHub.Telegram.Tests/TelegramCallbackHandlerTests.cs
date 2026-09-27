using FlowHub.Core.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowHub.Telegram.Tests;

public class TelegramCallbackHandlerTests
{
    private const long AllowedUser = 42L;

    private static TelegramCallback Callback(string? data, long from = AllowedUser) =>
        new(UpdateId: 9L, CallbackQueryId: "cbq-1", ChatId: 55L, MessageId: 8, FromUserId: from, Data: data);

    private static (TelegramCallbackHandler Sut, ITelegramUpdateRepository Repo, ITelegramGateway Gateway) Build()
    {
        var repo = Substitute.For<ITelegramUpdateRepository>();
        var gateway = Substitute.For<ITelegramGateway>();
        var options = Options.Create(new TelegramOptions { BotToken = "123:ABC", AllowedUserIds = [AllowedUser] });
        var sut = new TelegramCallbackHandler(repo, gateway, options, NullLogger<TelegramCallbackHandler>.Instance);
        return (sut, repo, gateway);
    }

    [Theory]
    [InlineData(TelegramMenu.LegendCallbackData)]
    public async Task HandleAsync_LegendButton_AnswersAndSendsTheLegend(string data)
    {
        var (sut, _, gateway) = Build();

        await sut.HandleAsync(Callback(data), CancellationToken.None);

        await gateway.Received(1).AnswerCallbackAsync("cbq-1", Arg.Any<CancellationToken>());
        await gateway.Received(1).SendTextAsync(55L,
            Arg.Is<string>(s => s.Contains(TelegramReactionService.InFlightEmoji) && s.Contains("💔")),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("something-else")]
    [InlineData(null)]
    public async Task HandleAsync_UnknownButton_OnlyStopsTheSpinner(string? data)
    {
        var (sut, _, gateway) = Build();

        await sut.HandleAsync(Callback(data), CancellationToken.None);

        await gateway.Received(1).AnswerCallbackAsync("cbq-1", Arg.Any<CancellationToken>());
        await gateway.DidNotReceiveWithAnyArgs().SendTextAsync(default, default!, default);
    }

    [Theory]
    [InlineData(TelegramMenu.LegendCallbackData)]
    public async Task HandleAsync_AnyCallback_IsRecordedWithoutACapture(string data)
    {
        // The poll offset is restored from the last recorded update: an unrecorded tap
        // would be replayed after a restart.
        var (sut, repo, _) = Build();

        await sut.HandleAsync(Callback(data), CancellationToken.None);

        await repo.Received(1).RecordAsync(
            Arg.Is<TelegramUpdate>(u => u.UpdateId == 9L && u.ChatId == 55L && u.MessageId == 8 && u.CaptureId == null),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(TelegramMenu.LegendCallbackData)]
    public async Task HandleAsync_AlreadyProcessed_DoesNothing(string data)
    {
        var (sut, repo, gateway) = Build();
        repo.ExistsAsync(9L, Arg.Any<CancellationToken>()).Returns(true);

        await sut.HandleAsync(Callback(data), CancellationToken.None);

        await gateway.DidNotReceiveWithAnyArgs().AnswerCallbackAsync(default!, default);
        await gateway.DidNotReceiveWithAnyArgs().SendTextAsync(default, default!, default);
        await repo.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
    }

    [Theory]
    [InlineData(TelegramMenu.LegendCallbackData)]
    public async Task HandleAsync_UnlistedUser_IsRecordedButNotAnswered(string data)
    {
        var (sut, repo, gateway) = Build();

        await sut.HandleAsync(Callback(data, from: 999L), CancellationToken.None);

        await gateway.DidNotReceiveWithAnyArgs().AnswerCallbackAsync(default!, default);
        await gateway.DidNotReceiveWithAnyArgs().SendTextAsync(default, default!, default);
        await repo.Received(1).RecordAsync(Arg.Any<TelegramUpdate>(), Arg.Any<CancellationToken>());
    }
}
