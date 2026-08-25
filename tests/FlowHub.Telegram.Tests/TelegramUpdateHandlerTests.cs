using FlowHub.Core.Captures;
using FlowHub.Core.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowHub.Telegram.Tests;

public class TelegramUpdateHandlerTests
{
    private const long AllowedUser = 42L;

    private static TelegramMessage TextMessage(string text, long from = AllowedUser, long updateId = 1L) =>
        new(UpdateId: updateId, ChatId: 55L, MessageId: 7, FromUserId: from, Text: text, File: null);

    private static (TelegramUpdateHandler Sut, ICaptureService Captures, ITelegramUpdateRepository Repo, ITelegramGateway Gateway) Build()
    {
        var captures = Substitute.For<ICaptureService>();
        captures.SubmitAsync(Arg.Any<string?>(), Arg.Any<ChannelKind>(), Arg.Any<AttachmentInput?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new Capture(
                Guid.NewGuid(), ci.ArgAt<ChannelKind>(1), ci.ArgAt<string?>(0) ?? "", DateTimeOffset.UtcNow,
                LifecycleStage.Raw, null)));
        var repo = Substitute.For<ITelegramUpdateRepository>();
        var gateway = Substitute.For<ITelegramGateway>();
        var options = Options.Create(new TelegramOptions { BotToken = "123:ABC", AllowedUserIds = [AllowedUser] });
        var uploads = Substitute.For<IUploadPolicy>();
        uploads.MaxBytes.Returns(2L * 1024 * 1024);
        uploads.AllowedContentTypes.Returns(["application/pdf", "image/png", "image/jpeg"]);
        var reactions = new TelegramReactionService(repo, gateway, NullLogger<TelegramReactionService>.Instance);

        var sut = new TelegramUpdateHandler(captures, repo, gateway, reactions, uploads, options,
            NullLogger<TelegramUpdateHandler>.Instance);
        return (sut, captures, repo, gateway);
    }

    [Fact]
    public async Task HandleAsync_LifecycleAlreadyResolvedWhenRowIsRecorded_StillReacts()
    {
        // The D6 race: EfCaptureService.SubmitAsync publishes CaptureCreated itself, so
        // with the in-memory transport the pipeline can drive a Capture to a terminal
        // stage before the handler has written the TelegramUpdate row. The decorator
        // fires first and finds no coordinates; the handler must re-check afterwards.
        var (sut, captures, repo, gateway) = Build();
        var captureId = Guid.NewGuid();
        captures.SubmitAsync(Arg.Any<string?>(), Arg.Any<ChannelKind>(), Arg.Any<AttachmentInput?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Capture(
                captureId, ChannelKind.Telegram, "done already", DateTimeOffset.UtcNow,
                LifecycleStage.Raw, null)));
        // By the time the row exists the pipeline has already completed the Capture.
        captures.GetByIdAsync(captureId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Capture?>(new Capture(
                captureId, ChannelKind.Telegram, "done already", DateTimeOffset.UtcNow,
                LifecycleStage.Completed, "wallabag")));
        repo.FindByCaptureIdAsync(captureId, Arg.Any<CancellationToken>())
            .Returns(new TelegramUpdate(1L, 55L, 7, captureId, DateTimeOffset.UtcNow));

        await sut.HandleAsync(TextMessage("done already"), CancellationToken.None);

        await gateway.Received(1).SetReactionAsync(55L, 7, "\U0001F44D", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_LifecycleStillInFlight_DoesNotReactYet()
    {
        // The ordinary path: the Capture is still Raw when the row lands, so the
        // handler must leave the reaction to the decorator rather than marking a
        // message whose outcome is not known yet.
        var (sut, captures, repo, gateway) = Build();
        var captureId = Guid.NewGuid();
        captures.SubmitAsync(Arg.Any<string?>(), Arg.Any<ChannelKind>(), Arg.Any<AttachmentInput?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Capture(
                captureId, ChannelKind.Telegram, "in flight", DateTimeOffset.UtcNow, LifecycleStage.Raw, null)));
        captures.GetByIdAsync(captureId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Capture?>(new Capture(
                captureId, ChannelKind.Telegram, "in flight", DateTimeOffset.UtcNow, LifecycleStage.Raw, null)));
        repo.FindByCaptureIdAsync(captureId, Arg.Any<CancellationToken>())
            .Returns(new TelegramUpdate(1L, 55L, 7, captureId, DateTimeOffset.UtcNow));

        await sut.HandleAsync(TextMessage("in flight"), CancellationToken.None);

        await gateway.DidNotReceiveWithAnyArgs().SetReactionAsync(default, default, default!, default);
    }

    [Fact]
    public async Task HandleAsync_AllowedUserSendsText_SubmitsCaptureWithTelegramChannel()
    {
        var (sut, captures, _, _) = Build();

        await sut.HandleAsync(TextMessage("https://example.com/article"), CancellationToken.None);

        await captures.Received(1).SubmitAsync(
            "https://example.com/article", ChannelKind.Telegram, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_AllowedUserSendsText_RecordsUpdateWithCaptureId()
    {
        var (sut, _, repo, _) = Build();

        await sut.HandleAsync(TextMessage("hello"), CancellationToken.None);

        await repo.Received(1).RecordAsync(
            Arg.Is<TelegramUpdate>(u => u.UpdateId == 1L && u.ChatId == 55L && u.MessageId == 7 && u.CaptureId != null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_DisallowedUser_SubmitsNothingButStillRecordsUpdate()
    {
        var (sut, captures, repo, gateway) = Build();

        await sut.HandleAsync(TextMessage("spam", from: 99L), CancellationToken.None);

        await captures.DidNotReceiveWithAnyArgs().SubmitAsync(default, default, default, default);
        await repo.Received(1).RecordAsync(
            Arg.Is<TelegramUpdate>(u => u.CaptureId == null), Arg.Any<CancellationToken>());
        await gateway.DidNotReceiveWithAnyArgs().SendTextAsync(default, default!, default);
    }

    [Fact]
    public async Task HandleAsync_AlreadyProcessedUpdate_DoesNothing()
    {
        var (sut, captures, repo, _) = Build();
        repo.ExistsAsync(1L, Arg.Any<CancellationToken>()).Returns(true);

        await sut.HandleAsync(TextMessage("replayed"), CancellationToken.None);

        await captures.DidNotReceiveWithAnyArgs().SubmitAsync(default, default, default, default);
        await repo.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
    }

    [Fact]
    public async Task HandleAsync_UnsupportedMessage_RepliesAndRecords()
    {
        var (sut, captures, repo, gateway) = Build();
        var voice = new TelegramMessage(2L, 55L, 8, AllowedUser, Text: null, File: null);

        await sut.HandleAsync(voice, CancellationToken.None);

        await gateway.Received(1).SendTextAsync(55L,
            Arg.Is<string>(s => s.Contains("not supported", StringComparison.OrdinalIgnoreCase)),
            Arg.Any<CancellationToken>());
        await captures.DidNotReceiveWithAnyArgs().SubmitAsync(default, default, default, default);
        await repo.Received(1).RecordAsync(Arg.Any<TelegramUpdate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_SubmitThrows_DoesNotRecordUpdate()
    {
        var (sut, captures, repo, _) = Build();
        captures.SubmitAsync(Arg.Any<string?>(), Arg.Any<ChannelKind>(), Arg.Any<AttachmentInput?>(), Arg.Any<CancellationToken>())
            .Returns<Task<Capture>>(_ => throw new InvalidOperationException("db down"));

        var act = async () => await sut.HandleAsync(TextMessage("boom"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await repo.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
    }
}
