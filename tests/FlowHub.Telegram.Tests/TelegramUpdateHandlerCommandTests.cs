using FlowHub.AI;
using FlowHub.Core.Captures;
using FlowHub.Core.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowHub.Telegram.Tests;

public class TelegramUpdateHandlerCommandTests
{
    private const long AllowedUser = 42L;

    private static TelegramMessage TextMessage(string text, long from = AllowedUser) =>
        new(UpdateId: 1L, ChatId: 55L, MessageId: 7, FromUserId: from, Text: text, File: null);

    private static (TelegramUpdateHandler Sut, ICaptureService Captures, ITelegramUpdateRepository Repo, ITelegramGateway Gateway) Build()
    {
        var captures = Substitute.For<ICaptureService>();
        var repo = Substitute.For<ITelegramUpdateRepository>();
        var gateway = Substitute.For<ITelegramGateway>();
        var options = Options.Create(new TelegramOptions { BotToken = "123:ABC", AllowedUserIds = [AllowedUser] });
        var speech = Options.Create(new SpeechOptions { MaxSeconds = 300 });
        var uploads = Substitute.For<IUploadPolicy>();
        var reactions = new TelegramReactionService(repo, gateway, NullLogger<TelegramReactionService>.Instance);

        var sut = new TelegramUpdateHandler(captures, repo, gateway, reactions, uploads, options, speech,
            NullLogger<TelegramUpdateHandler>.Instance);
        return (sut, captures, repo, gateway);
    }

    [Theory]
    [InlineData("/menu")]
    [InlineData("/start")]
    [InlineData("/menu@FlowHubBot")]
    [InlineData("/MENU extra words")]
    public async Task HandleAsync_MenuCommand_SendsMenuWithEmojisButton(string text)
    {
        var (sut, _, _, gateway) = Build();

        await sut.HandleAsync(TextMessage(text), CancellationToken.None);

        await gateway.Received(1).SendMenuAsync(55L, Arg.Any<string>(),
            Arg.Is<IReadOnlyList<TelegramMenuButton>>(b =>
                b.Any(x => x.Label == "Emojis" && x.CallbackData == TelegramMenu.LegendCallbackData)),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("/menu")]
    [InlineData("/nope")]
    public async Task HandleAsync_AnyCommand_IsRecordedAndNeverBecomesACapture(string text)
    {
        var (sut, captures, repo, _) = Build();

        await sut.HandleAsync(TextMessage(text), CancellationToken.None);

        await captures.DidNotReceiveWithAnyArgs().SubmitAsync(default, default, default, default, default);
        await repo.Received(1).RecordAsync(
            Arg.Is<TelegramUpdate>(u => u.UpdateId == 1L && u.CaptureId == null), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("/nope")]
    public async Task HandleAsync_UnknownCommand_RepliesWithMenuHint(string text)
    {
        var (sut, _, _, gateway) = Build();

        await sut.HandleAsync(TextMessage(text), CancellationToken.None);

        await gateway.Received(1).SendTextAsync(55L, Arg.Is<string>(s => s.Contains("/menu")), Arg.Any<CancellationToken>());
        await gateway.DidNotReceiveWithAnyArgs().SendMenuAsync(default, default!, default!, default);
    }

    [Theory]
    [InlineData("/menu")]
    public async Task HandleAsync_CommandFromUnlistedUser_GetsNoReply(string text)
    {
        var (sut, _, _, gateway) = Build();

        await sut.HandleAsync(TextMessage(text, from: 999L), CancellationToken.None);

        await gateway.DidNotReceiveWithAnyArgs().SendMenuAsync(default, default!, default!, default);
        await gateway.DidNotReceiveWithAnyArgs().SendTextAsync(default, default!, default);
    }
}
