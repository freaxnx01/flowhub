using FlowHub.Api.Endpoints;
using FlowHub.Core.Captures;
using FlowHub.Core.Events;
using MassTransit;
using Microsoft.AspNetCore.Http;

namespace FlowHub.Api.IntegrationTests.Captures;

/// <summary>
/// The retry path re-publishes CaptureCreated to re-enter the pipeline. The flags on
/// that event are how consumers decide what a Capture is — dropping one silently
/// changes the routing of a retried Capture (#34).
/// </summary>
public sealed class RetryRepublishTests
{
    private static Capture OrphanWithAttachment() => new(
        Guid.NewGuid(), ChannelKind.Web, "invoice.pdf", DateTimeOffset.UtcNow,
        LifecycleStage.Orphan, MatchedSkill: null, FailureReason: "boom",
        Attachment: new Attachment("invoice.pdf", "application/pdf", 10, "2026/08/x.pdf", DateTimeOffset.UtcNow));

    private static Capture OrphanWithoutAttachment() => new(
        Guid.NewGuid(), ChannelKind.Web, "https://example.com", DateTimeOffset.UtcNow,
        LifecycleStage.Orphan, MatchedSkill: null, FailureReason: "boom");

    private static async Task<CaptureCreated> RetryAndCaptureEventAsync(Capture capture)
    {
        var captures = Substitute.For<ICaptureService>();
        captures.GetByIdAsync(capture.Id, Arg.Any<CancellationToken>()).Returns(capture);
        var bus = Substitute.For<IBus>();
        CaptureCreated? published = null;
        await bus.Publish(Arg.Do<CaptureCreated>(e => published = e), Arg.Any<CancellationToken>());
        bus.ClearReceivedCalls();

        await CaptureRetryEndpoint.RetryAsync(
            capture.Id, captures, bus, new DefaultHttpContext(), CancellationToken.None);

        published.Should().NotBeNull();
        return published!;
    }

    [Fact]
    public async Task Retry_CaptureWithAttachment_RepublishesWithHasAttachmentTrue()
    {
        var published = await RetryAndCaptureEventAsync(OrphanWithAttachment());

        published.HasAttachment.Should().BeTrue();
    }

    [Fact]
    public async Task Retry_CaptureWithoutAttachment_RepublishesWithHasAttachmentFalse()
    {
        var published = await RetryAndCaptureEventAsync(OrphanWithoutAttachment());

        published.HasAttachment.Should().BeFalse();
    }
}
