using FlowHub.Core.Captures;
using FlowHub.Core.Channels;
using FlowHub.Core.Events;
using FlowHub.Telegram;
using FlowHub.Web.Pipeline;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FlowHub.Web.ComponentTests.Pipeline;

public sealed class CaptureTranscriptionConsumerTests
{
    // Matches on any capture id: the Telegram row is a stand-in for whichever
    // placeholder the test creates, and echoing the incoming id back avoids a race
    // against configuring the mock before the id is known.
    private static (ISpeechToText Stt, ITelegramGateway Gateway, ITelegramUpdateRepository Repo) Stubs(
        string? transcript)
    {
        var stt = Substitute.For<ISpeechToText>();
        stt.TranscribeAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(transcript));
        var gateway = Substitute.For<ITelegramGateway>();
        gateway.DownloadFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream?>(new MemoryStream(new byte[8])));
        var repo = Substitute.For<ITelegramUpdateRepository>();
        repo.FindByCaptureIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => new TelegramUpdate(1L, 55L, 4, ci.Arg<Guid>(), DateTimeOffset.UtcNow, FileId: "voice-abc"));
        return (stt, gateway, repo);
    }

    [Fact]
    public async Task Consume_SuccessfulTranscription_RepublishesWithoutTheFlag()
    {
        var (stt, gateway, repo) = Stubs("buy milk on the way home");
        await using var provider = PipelineTestBase.Build(
            configure: s => { s.AddSingleton(stt); s.AddSingleton(gateway); s.AddSingleton(repo); },
            configureBus: x => x.AddConsumer<CaptureTranscriptionConsumer>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        // Goes through the real placeholder-creation path (CaptureServiceStub) rather
        // than publishing a synthetic CaptureCreated, so the row the consumer later
        // calls SetTranscriptAsync on actually exists.
        var captureService = provider.GetRequiredService<ICaptureService>();
        var placeholder = await captureService.SubmitAsync(
            "[voice message]", ChannelKind.Telegram, attachment: null, needsTranscription: true);

        (await harness.Published.Any<CaptureCreated>(
            x => x.Context.Message.CaptureId == placeholder.Id
                 && !x.Context.Message.NeedsTranscription
                 && x.Context.Message.Content == "buy milk on the way home"))
            .Should().BeTrue();

        // The row must carry it too, not just the event (D8).
        (await captureService.GetByIdAsync(placeholder.Id))?.Content
            .Should().Be("buy milk on the way home");
    }

    [Fact]
    public async Task Consume_TranscriptionReturnsNull_MarksOrphanAndDoesNotRepublish()
    {
        var captureId = Guid.NewGuid();
        var (stt, gateway, repo) = Stubs(null);
        var captures = Substitute.For<ICaptureService>();
        await using var provider = PipelineTestBase.Build(
            configure: s => { s.AddSingleton(stt); s.AddSingleton(gateway); s.AddSingleton(repo); s.AddSingleton(captures); },
            configureBus: x => x.AddConsumer<CaptureTranscriptionConsumer>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new CaptureCreated(
            captureId, "[voice message]", ChannelKind.Telegram, DateTimeOffset.UtcNow,
            HasAttachment: false, NeedsTranscription: true));

        (await harness.Consumed.Any<CaptureCreated>(x => x.Context.Message.CaptureId == captureId))
            .Should().BeTrue();
        await captures.Received(1).MarkOrphanAsync(captureId, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Consume_WithoutTheFlag_DoesNothing()
    {
        var captureId = Guid.NewGuid();
        var (stt, gateway, repo) = Stubs("ignored");
        await using var provider = PipelineTestBase.Build(
            configure: s => { s.AddSingleton(stt); s.AddSingleton(gateway); s.AddSingleton(repo); },
            configureBus: x => x.AddConsumer<CaptureTranscriptionConsumer>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new CaptureCreated(
            captureId, "ordinary text", ChannelKind.Telegram, DateTimeOffset.UtcNow));

        await stt.DidNotReceiveWithAnyArgs().TranscribeAsync(default!, default!, default);
    }
}
