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

    [Fact]
    public async Task Consume_NoTelegramFileRecorded_MarksOrphanAndDoesNotRepublish()
    {
        var (stt, gateway, repo) = Stubs("unused");
        repo.FindByCaptureIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((TelegramUpdate?)null);
        var captures = Substitute.For<ICaptureService>();
        await using var provider = PipelineTestBase.Build(
            configure: s => { s.AddSingleton(stt); s.AddSingleton(gateway); s.AddSingleton(repo); s.AddSingleton(captures); },
            configureBus: x => x.AddConsumer<CaptureTranscriptionConsumer>());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var captureId = Guid.NewGuid();

        await harness.Bus.Publish(new CaptureCreated(
            captureId, VoiceCapture.PlaceholderContent, ChannelKind.Telegram, DateTimeOffset.UtcNow,
            HasAttachment: false, NeedsTranscription: true));

        // Publish returns before the consumer runs — wait for consumption, as the
        // sibling tests do, or the assertion races the pipeline.
        (await harness.Consumed.Any<CaptureCreated>(x => x.Context.Message.CaptureId == captureId))
            .Should().BeTrue();

        await captures.Received(1).MarkOrphanAsync(captureId, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await stt.DidNotReceiveWithAnyArgs().TranscribeAsync(default!, default!, default);
    }

    [Fact]
    public async Task Consume_DownloadReturnsNull_MarksOrphanAndDoesNotTranscribe()
    {
        var (stt, gateway, repo) = Stubs("unused");
        gateway.DownloadFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream?>(null));
        var captures = Substitute.For<ICaptureService>();
        await using var provider = PipelineTestBase.Build(
            configure: s => { s.AddSingleton(stt); s.AddSingleton(gateway); s.AddSingleton(repo); s.AddSingleton(captures); },
            configureBus: x => x.AddConsumer<CaptureTranscriptionConsumer>());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var captureId = Guid.NewGuid();

        await harness.Bus.Publish(new CaptureCreated(
            captureId, VoiceCapture.PlaceholderContent, ChannelKind.Telegram, DateTimeOffset.UtcNow,
            HasAttachment: false, NeedsTranscription: true));

        // Publish returns before the consumer runs — wait for consumption, as the
        // sibling tests do, or the assertion races the pipeline.
        (await harness.Consumed.Any<CaptureCreated>(x => x.Context.Message.CaptureId == captureId))
            .Should().BeTrue();

        await captures.Received(1).MarkOrphanAsync(captureId, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await stt.DidNotReceiveWithAnyArgs().TranscribeAsync(default!, default!, default);
    }

    [Fact]
    public async Task Consume_ReplyFails_StillMarksOrphan()
    {
        // The chat reply is best-effort; a Telegram outage must not stop the capture
        // reaching a terminal stage, or it would sit in Raw with no signal anywhere.
        var (stt, gateway, repo) = Stubs(null);
        gateway.SendTextAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new HttpRequestException("telegram down"));
        var captures = Substitute.For<ICaptureService>();
        await using var provider = PipelineTestBase.Build(
            configure: s => { s.AddSingleton(stt); s.AddSingleton(gateway); s.AddSingleton(repo); s.AddSingleton(captures); },
            configureBus: x => x.AddConsumer<CaptureTranscriptionConsumer>());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var captureId = Guid.NewGuid();

        await harness.Bus.Publish(new CaptureCreated(
            captureId, VoiceCapture.PlaceholderContent, ChannelKind.Telegram, DateTimeOffset.UtcNow,
            HasAttachment: false, NeedsTranscription: true));

        // Publish returns before the consumer runs — wait for consumption, as the
        // sibling tests do, or the assertion races the pipeline.
        (await harness.Consumed.Any<CaptureCreated>(x => x.Context.Message.CaptureId == captureId))
            .Should().BeTrue();

        await captures.Received(1).MarkOrphanAsync(captureId, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

}
