using FlowHub.Core.Captures;
using FlowHub.Core.Classification;
using FlowHub.Core.Events;
using FlowHub.Core.Skills;
using FlowHub.Web.Pipeline;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FlowHub.Web.ComponentTests.Pipeline;

public sealed class SkillRoutingConsumerBridgeTests
{
    [Fact]
    public async Task Consume_BridgeSkill_GraftsBridgeFieldsOntoCaptureFromEvent()
    {
        Capture? seen = null;
        var integration = Substitute.For<ISkillIntegration>();
        integration.Name.Returns("Bridge");
        integration.HandleAsync(Arg.Any<Capture>(), Arg.Any<CancellationToken>())
            .Returns(ci => { seen = ci.Arg<Capture>(); return Task.FromResult(new SkillResult(true, "https://forge/issue/1")); });

        await using var provider = PipelineTestBase.Build(
            configure: s => s.AddSingleton(integration),
            configureBus: x => x.AddConsumer<SkillRoutingConsumer>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var captureService = provider.GetRequiredService<ICaptureService>();
        var capture = await captureService.SubmitAsync("br the login 500s", ChannelKind.Web, default);
        await captureService.MarkClassifiedAsync(capture.Id, "Bridge", title: "Login 500", default);

        await harness.Bus.Publish(new CaptureClassified(
            capture.Id, ["bridge"], "Bridge", DateTimeOffset.UtcNow,
            BridgeAlias: "br", BridgeAction: BridgeAction.Issue, BridgeBody: "the login 500s"));

        (await harness.Consumed.Any<CaptureClassified>(x => x.Context.Message.CaptureId == capture.Id))
            .Should().BeTrue();

        seen.Should().NotBeNull();
        seen!.BridgeAlias.Should().Be("br");
        seen.BridgeAction.Should().Be(BridgeAction.Issue);
        seen.BridgeBody.Should().Be("the login 500s");

        (await captureService.GetByIdAsync(capture.Id, default))!.Stage.Should().Be(LifecycleStage.Completed);
    }
}
