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
            configure: s => s.AddSingleton(screen),
            configureBus: x => x.AddConsumer<CaptureEnrichmentConsumer>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        // Publish directly so NeedsTranscription can be set — a voice capture holds
        // placeholder text until the transcription consumer republishes it.
        var captureService = provider.GetRequiredService<ICaptureService>();
        var capture = await captureService.SubmitAsync("[voice message]", ChannelKind.Telegram, default);
        await harness.Bus.Publish(new CaptureCreated(
            capture.Id, "[voice message]", ChannelKind.Telegram, DateTimeOffset.UtcNow, NeedsTranscription: true));

        (await harness.Consumed.Any<CaptureCreated>(x => x.Context.Message.NeedsTranscription))
            .Should().BeTrue();

        // Screening the placeholder would be meaningless; the transcript gets screened
        // when the transcription consumer republishes without the flag.
        await screen.DidNotReceive().ScreenAsync("[voice message]", Arg.Any<CancellationToken>());
    }
}
