using FlowHub.Core.Captures;
using FlowHub.Core.Classification;
using FlowHub.Core.Events;
using FlowHub.Web.Pipeline;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FlowHub.Web.ComponentTests.Pipeline;

public sealed class CaptureEnrichmentConsumerBridgeTests
{
    private static IClassifier ClassifierReturning(ClassificationResult result)
    {
        var classifier = Substitute.For<IClassifier>();
        classifier.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(result);
        return classifier;
    }

    [Fact]
    public async Task Consume_BridgeIssue_PublishesClassifiedWithBridgeFields()
    {
        var classifier = ClassifierReturning(new ClassificationResult(
            ["bridge"], "Bridge", Title: "Login 500", BridgeAlias: "br",
            BridgeAction: BridgeAction.Issue, BridgeBody: "the login 500s"));

        await using var provider = PipelineTestBase.Build(
            configure: s => s.AddSingleton(classifier),
            configureBus: x => x.AddConsumer<CaptureEnrichmentConsumer>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var captureService = provider.GetRequiredService<ICaptureService>();
        var capture = await captureService.SubmitAsync("br the login 500s", ChannelKind.Web, default);

        await harness.Bus.Publish(new CaptureCreated(capture.Id, "br the login 500s", ChannelKind.Web, DateTimeOffset.UtcNow));

        (await harness.Published.Any<CaptureClassified>(x =>
            x.Context.Message.CaptureId == capture.Id
            && x.Context.Message.MatchedSkill == "Bridge"
            && x.Context.Message.BridgeAlias == "br"
            && x.Context.Message.BridgeAction == BridgeAction.Issue
            && x.Context.Message.BridgeBody == "the login 500s")).Should().BeTrue();
    }

    [Fact]
    public async Task Consume_BridgeUnknown_MarksUnhandledAndDoesNotPublish()
    {
        var classifier = ClassifierReturning(new ClassificationResult(
            ["bridge"], "Bridge", BridgeAlias: "br", BridgeAction: BridgeAction.Unknown));

        await using var provider = PipelineTestBase.Build(
            configure: s => s.AddSingleton(classifier),
            configureBus: x => x.AddConsumer<CaptureEnrichmentConsumer>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var captureService = provider.GetRequiredService<ICaptureService>();
        var capture = await captureService.SubmitAsync("br hmm", ChannelKind.Web, default);

        await harness.Bus.Publish(new CaptureCreated(capture.Id, "br hmm", ChannelKind.Web, DateTimeOffset.UtcNow));

        (await harness.Consumed.Any<CaptureCreated>(x => x.Context.Message.CaptureId == capture.Id))
            .Should().BeTrue();

        (await captureService.GetByIdAsync(capture.Id, default))!.Stage.Should().Be(LifecycleStage.Unhandled);
        (await harness.Published.Any<CaptureClassified>(x => x.Context.Message.CaptureId == capture.Id))
            .Should().BeFalse();
    }
}
