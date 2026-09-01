using FlowHub.AI;
using FlowHub.Core.Captures;
using FlowHub.Core.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowHub.Telegram.Tests;

public class TelegramUpdateHandlerVoiceTests
{
    private const long AllowedUser = 42L;

    private static (TelegramUpdateHandler Sut, ICaptureService Captures, ITelegramGateway Gateway) Build(
        SpeechOptions? speechOptions = null)
    {
        var captures = Substitute.For<ICaptureService>();
        captures.SubmitAsync(Arg.Any<string?>(), Arg.Any<ChannelKind>(), Arg.Any<AttachmentInput?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new Capture(
                Guid.NewGuid(), ChannelKind.Telegram, ci.ArgAt<string?>(0) ?? "", DateTimeOffset.UtcNow,
                LifecycleStage.Raw, null)));
        var repo = Substitute.For<ITelegramUpdateRepository>();
        var gateway = Substitute.For<ITelegramGateway>();
        var uploads = Substitute.For<IUploadPolicy>();
        uploads.MaxBytes.Returns(2L * 1024 * 1024);
        uploads.AllowedContentTypes.Returns(["application/pdf", "image/png", "image/jpeg"]);
        var options = Options.Create(new TelegramOptions { BotToken = "123:ABC", AllowedUserIds = [AllowedUser] });
        var speech = Options.Create(speechOptions ?? new SpeechOptions { MaxSeconds = 300, ApiKey = "sk-test" });
        var reactions = new TelegramReactionService(repo, gateway, NullLogger<TelegramReactionService>.Instance);

        return (new TelegramUpdateHandler(captures, repo, gateway, reactions, uploads, options, speech,
            NullLogger<TelegramUpdateHandler>.Instance), captures, gateway);
    }

    private static TelegramMessage VoiceMessage(int duration) =>
        new(UpdateId: 9L, ChatId: 55L, MessageId: 4, FromUserId: AllowedUser, Text: null,
            File: new TelegramFile("voice-abc", "voice-4.ogg", "audio/ogg", 2048, duration));

    [Fact]
    public async Task HandleAsync_VoiceWithinTheCap_SubmitsForTranscription()
    {
        var (sut, captures, _) = Build();

        await sut.HandleAsync(VoiceMessage(30), CancellationToken.None);

        await captures.Received(1).SubmitAsync(
            Arg.Any<string?>(), ChannelKind.Telegram, Arg.Any<AttachmentInput?>(),
            needsTranscription: true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_VoiceOverTheCap_RepliesWithTheLimitAndSubmitsNothing()
    {
        var (sut, captures, gateway) = Build();

        await sut.HandleAsync(VoiceMessage(3000), CancellationToken.None);

        await gateway.Received(1).SendTextAsync(55L,
            Arg.Is<string>(s => s.Contains("300", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await captures.DidNotReceiveWithAnyArgs().SubmitAsync(default, default, default, default, default);
    }

    [Fact]
    public async Task HandleAsync_VoiceDoesNotDownloadInTheHandler()
    {
        var (sut, _, gateway) = Build();

        await sut.HandleAsync(VoiceMessage(30), CancellationToken.None);

        // Downloading belongs to the transcription consumer; doing it here would
        // block the single-threaded poll loop (design D3).
        await gateway.DidNotReceiveWithAnyArgs().DownloadFileAsync(default!, default);
    }

    [Fact]
    public async Task HandleAsync_VoiceWithSpeechUnconfigured_RepliesUnsupportedAndSubmitsNothing()
    {
        var (sut, captures, gateway) = Build(new SpeechOptions { MaxSeconds = 300 });

        await sut.HandleAsync(VoiceMessage(30), CancellationToken.None);

        await gateway.Received(1).SendTextAsync(55L,
            Arg.Is<string>(s => s.Contains("not supported", StringComparison.OrdinalIgnoreCase)),
            Arg.Any<CancellationToken>());
        await captures.DidNotReceiveWithAnyArgs().SubmitAsync(default, default, default, default, default);
    }
}
