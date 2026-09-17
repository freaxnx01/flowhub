using FlowHub.Core.Captures;
using FlowHub.Core.Classification;
using FlowHub.Core.Events;
using FlowHub.Web.Pipeline;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FlowHub.Web.ComponentTests.Pipeline;

public sealed class CaptureEnrichmentConsumerSensitivityTests
{
    private static ISensitivityScreen ScreenReturning(Sensitivity verdict, string reason)
    {
        var screen = Substitute.For<ISensitivityScreen>();
        screen.ScreenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(new SensitivityVerdict(verdict, reason));
        return screen;
    }

    private static IClassifier TrackingClassifier()
    {
        var classifier = Substitute.For<IClassifier>();
        classifier.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                  .Returns(new ClassificationResult(["t"], "Vikunja", VikunjaProject: "Inbox"));
        return classifier;
    }

    [Theory]
    [InlineData(Sensitivity.Sensitive)]
    [InlineData(Sensitivity.Unsure)]
    public async Task Consume_NonSafeVerdict_WithholdsAndNeverClassifies(Sensitivity verdict)
    {
        var classifier = TrackingClassifier();
        var screen = ScreenReturning(verdict, "child care material");

        await using var provider = PipelineTestBase.Build(
            configure: s => { s.AddSingleton(classifier); s.AddSingleton(screen); },
            configureBus: x => x.AddConsumer<CaptureEnrichmentConsumer>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var captureService = provider.GetRequiredService<ICaptureService>();
        var capture = await captureService.SubmitAsync("anything at all", ChannelKind.Web, default);

        (await harness.Consumed.Any<CaptureCreated>(x => x.Context.Message.CaptureId == capture.Id))
            .Should().BeTrue();

        var stored = await captureService.GetByIdAsync(capture.Id, default);
        stored!.Stage.Should().Be(LifecycleStage.Withheld);
        stored.FailureReason.Should().Contain("child care material");

        // The two assertions that matter: no classification, and nothing published
        // for the routing stage to pick up and hand to an integration.
        await classifier.DidNotReceive().ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        (await harness.Published.Any<CaptureClassified>(x => x.Context.Message.CaptureId == capture.Id))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Consume_SafeVerdict_ClassifiesAsBefore()
    {
        var classifier = TrackingClassifier();
        var screen = ScreenReturning(Sensitivity.Safe, string.Empty);

        await using var provider = PipelineTestBase.Build(
            configure: s => { s.AddSingleton(classifier); s.AddSingleton(screen); },
            configureBus: x => x.AddConsumer<CaptureEnrichmentConsumer>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var captureService = provider.GetRequiredService<ICaptureService>();
        var capture = await captureService.SubmitAsync("an ordinary errand", ChannelKind.Web, default);

        (await harness.Published.Any<CaptureClassified>(x => x.Context.Message.CaptureId == capture.Id))
            .Should().BeTrue();
        await classifier.Received(1).ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Consume_CaptureAwaitingTranscription_IsNotScreenedOnItsPlaceholder()
    {
        var screen = ScreenReturning(Sensitivity.Safe, string.Empty);

        await using var provider = PipelineTestBase.Build(
            configure: s => { s.AddSingleton(TrackingClassifier()); s.AddSingleton(screen); },
            configureBus: x => x.AddConsumer<CaptureEnrichmentConsumer>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        // Publish ONLY the awaiting-transcript message, with content no other publication
        // uses. Going through SubmitAsync would also publish a NeedsTranscription:false
        // message carrying the same text, which the consumer would legitimately screen —
        // so the assertion below would contradict itself. Registering an IClassifier is
        // likewise mandatory: without one the consumer cannot be constructed, every
        // message faults, and DidNotReceive() passes because nothing ran at all.
        var id = Guid.NewGuid();
        await harness.Bus.Publish(new CaptureCreated(
            id, "[awaiting transcript]", ChannelKind.Telegram, DateTimeOffset.UtcNow,
            NeedsTranscription: true));

        (await harness.Consumed.Any<CaptureCreated>(x => x.Context.Message.CaptureId == id))
            .Should().BeTrue();

        // Screening the placeholder would be meaningless; the transcript gets screened
        // when the transcription consumer republishes without the flag.
        await screen.DidNotReceive().ScreenAsync("[awaiting transcript]", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Consume_UncaptionedAttachment_ReachesPaperlessWithoutBeingScreened()
    {
        var screen = ScreenReturning(Sensitivity.Sensitive, "would park it");

        await using var provider = PipelineTestBase.Build(
            configure: s => { s.AddSingleton(TrackingClassifier()); s.AddSingleton(screen); },
            configureBus: x => x.AddConsumer<CaptureEnrichmentConsumer>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        // Spec D7. A null caption makes Content the *filename* (EfCaptureService.SubmitAsync),
        // so screening this path would judge "scan_0012.pdf" and answer Unsure, parking
        // every uncaptioned scan in a terminal, non-retryable stage. Even a Sensitive
        // verdict must not reach it: the HasAttachment branch short-circuits first.
        // Reversing the order in Consume fails this test.
        var captureService = provider.GetRequiredService<ICaptureService>();
        using var bytes = new MemoryStream(new byte[] { 1, 2, 3 });
        var capture = await captureService.SubmitAsync(
            caption: null, ChannelKind.Telegram,
            new AttachmentInput { Content = bytes, FileName = "scan_0012.pdf", ContentType = "application/pdf", SizeBytes = 3 },
            cancellationToken: default);

        (await harness.Published.Any<CaptureClassified>(
            x => x.Context.Message.CaptureId == capture.Id
                && x.Context.Message.MatchedSkill == "Paperless"))
            .Should().BeTrue();

        await screen.DidNotReceive().ScreenAsync("scan_0012.pdf", Arg.Any<CancellationToken>());

        var stored = await captureService.GetByIdAsync(capture.Id, default);
        stored!.Stage.Should().NotBe(LifecycleStage.Withheld);
    }
}
