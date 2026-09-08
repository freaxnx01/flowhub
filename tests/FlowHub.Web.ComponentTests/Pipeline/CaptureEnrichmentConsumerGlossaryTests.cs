using FlowHub.Core.Classification;
using FlowHub.Core.Events;
using FlowHub.Web.Pipeline;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FlowHub.Web.ComponentTests.Pipeline;

public sealed class CaptureEnrichmentConsumerGlossaryTests
{
    private static IClassifier ClassifierReturning(ClassificationResult result)
    {
        var classifier = Substitute.For<IClassifier>();
        classifier.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(result);
        return classifier;
    }

    [Fact]
    public async Task Consume_UnknownShorthand_ParksWithAReasonNamingTheToken()
    {
        var classifier = ClassifierReturning(new ClassificationResult(
            ["t"], "Vikunja", Title: "Farmshop", VikunjaProject: "Inbox",
            UnknownShorthand: ["qq"]));

        await using var provider = PipelineTestBase.Build(
            configure: s => s.AddSingleton(classifier),
            configureBus: x => x.AddConsumer<CaptureEnrichmentConsumer>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var captureService = provider.GetRequiredService<ICaptureService>();
        var capture = await captureService.SubmitAsync("Farmshop qq", ChannelKind.Web, default);

        (await harness.Consumed.Any<CaptureCreated>(x => x.Context.Message.CaptureId == capture.Id))
            .Should().BeTrue();

        var stored = await captureService.GetByIdAsync(capture.Id, default);
        stored!.Stage.Should().Be(LifecycleStage.Unhandled);
        stored.FailureReason.Should().Contain("qq");

        (await harness.Published.Any<CaptureClassified>(x => x.Context.Message.CaptureId == capture.Id))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Consume_NoUnknownShorthand_IsUnaffected()
    {
        var classifier = ClassifierReturning(new ClassificationResult(
            ["t"], "Vikunja", Title: "Farmshop", VikunjaProject: "Inbox"));

        await using var provider = PipelineTestBase.Build(
            configure: s => s.AddSingleton(classifier),
            configureBus: x => x.AddConsumer<CaptureEnrichmentConsumer>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var captureService = provider.GetRequiredService<ICaptureService>();
        var capture = await captureService.SubmitAsync("Farmshop", ChannelKind.Web, default);

        (await harness.Published.Any<CaptureClassified>(x => x.Context.Message.CaptureId == capture.Id))
            .Should().BeTrue();
    }
}
