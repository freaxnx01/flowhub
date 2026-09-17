using FlowHub.Core.Captures;
using MassTransit;

namespace FlowHub.Persistence.Tests;

public sealed class EfCaptureServiceWithheldTests
{
    private static (ICaptureRepository repo, EfCaptureService sut, IPublishEndpoint ep) Build()
    {
        var ep = Substitute.For<IPublishEndpoint>();
        var repo = Substitute.For<ICaptureRepository>();
        var storage = Substitute.For<IAttachmentStorage>();
        return (repo, new EfCaptureService(repo, ep, storage), ep);
    }

    private static Capture MakeCapture(Guid? id = null, LifecycleStage stage = LifecycleStage.Raw) =>
        new(id ?? Guid.NewGuid(), ChannelKind.Web, "content", DateTimeOffset.UtcNow, stage, null);

    [Fact]
    public async Task MarkWithheldAsync_SetsWithheldStageAndReason()
    {
        var (repo, sut, _) = Build();
        var id = Guid.NewGuid();
        var capture = MakeCapture(id);
        repo.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(capture);
        var updated = capture with { Stage = LifecycleStage.Withheld, FailureReason = "sensitive — care journal" };
        repo.UpdateAsync(updated, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await sut.MarkWithheldAsync(id, "sensitive — care journal", default);

        await repo.Received(1).UpdateAsync(
            Arg.Is<Capture>(c =>
                c.Id == id &&
                c.Stage == LifecycleStage.Withheld &&
                c.FailureReason == "sensitive — care journal"),
            Arg.Any<CancellationToken>());
    }
}
