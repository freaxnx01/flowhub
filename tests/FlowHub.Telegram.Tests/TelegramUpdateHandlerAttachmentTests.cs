using FlowHub.Core.Captures;
using FlowHub.Core.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowHub.Telegram.Tests;

public class TelegramUpdateHandlerAttachmentTests
{
    private const long AllowedUser = 42L;

    private static (TelegramUpdateHandler Sut, ICaptureService Captures, ITelegramGateway Gateway) Build(long maxBytes = 2L * 1024 * 1024)
    {
        var captures = Substitute.For<ICaptureService>();
        captures.SubmitAsync(Arg.Any<string?>(), Arg.Any<ChannelKind>(), Arg.Any<AttachmentInput?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new Capture(
                Guid.NewGuid(), ChannelKind.Telegram, "file.pdf", DateTimeOffset.UtcNow, LifecycleStage.Raw, null)));
        var repo = Substitute.For<ITelegramUpdateRepository>();
        var gateway = Substitute.For<ITelegramGateway>();
        gateway.DownloadFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream?>(new MemoryStream(new byte[16])));
        var uploads = Substitute.For<IUploadPolicy>();
        uploads.MaxBytes.Returns(maxBytes);
        uploads.AllowedContentTypes.Returns(["application/pdf", "image/png", "image/jpeg"]);
        var options = Options.Create(new TelegramOptions { BotToken = "123:ABC", AllowedUserIds = [AllowedUser] });

        return (new TelegramUpdateHandler(captures, repo, gateway, uploads, options,
            NullLogger<TelegramUpdateHandler>.Instance), captures, gateway);
    }

    private static TelegramMessage FileMessage(string name, string contentType, long size, string? caption = null) =>
        new(UpdateId: 5L, ChatId: 55L, MessageId: 9, FromUserId: AllowedUser, Text: caption,
            File: new TelegramFile("file-abc", name, contentType, size));

    [Fact]
    public async Task HandleAsync_Document_SubmitsWithAttachmentInput()
    {
        var (sut, captures, _) = Build();

        await sut.HandleAsync(FileMessage("invoice.pdf", "application/pdf", 16), CancellationToken.None);

        await captures.Received(1).SubmitAsync(
            Arg.Any<string?>(), ChannelKind.Telegram,
            Arg.Is<AttachmentInput>(a => a.FileName == "invoice.pdf" && a.ContentType == "application/pdf" && a.SizeBytes == 16),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_FileTooLarge_RepliesWithTheLimitAndSubmitsNothing()
    {
        var (sut, captures, gateway) = Build(maxBytes: 10);

        await sut.HandleAsync(FileMessage("big.pdf", "application/pdf", 99), CancellationToken.None);

        await gateway.Received(1).SendTextAsync(55L, Arg.Is<string>(s => s.Contains("10", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await captures.DidNotReceiveWithAnyArgs().SubmitAsync(default, default, default, default);
    }

    [Fact]
    public async Task HandleAsync_DisallowedContentType_RepliesAndSubmitsNothing()
    {
        var (sut, captures, gateway) = Build();

        await sut.HandleAsync(FileMessage("archive.zip", "application/zip", 16), CancellationToken.None);

        await gateway.Received(1).SendTextAsync(55L, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await captures.DidNotReceiveWithAnyArgs().SubmitAsync(default, default, default, default);
    }

    [Fact]
    public async Task HandleAsync_DownloadReturnsNull_SubmitsNothing()
    {
        var (sut, captures, gateway) = Build();
        gateway.DownloadFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream?>(null));

        await sut.HandleAsync(FileMessage("gone.pdf", "application/pdf", 16), CancellationToken.None);

        await captures.DidNotReceiveWithAnyArgs().SubmitAsync(default, default, default, default);
    }
}
