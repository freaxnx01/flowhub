using FlowHub.Api.Endpoints;
using FlowHub.Core.Captures;
using FlowHub.Core.Channels;
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

    private static async Task<CaptureCreated> RetryAndCaptureEventAsync(
        Capture capture, TelegramUpdate? telegramUpdate = null)
    {
        var captures = Substitute.For<ICaptureService>();
        captures.GetByIdAsync(capture.Id, Arg.Any<CancellationToken>()).Returns(capture);
        var updates = Substitute.For<ITelegramUpdateRepository>();
        updates.FindByCaptureIdAsync(capture.Id, Arg.Any<CancellationToken>()).Returns(telegramUpdate);
        var bus = Substitute.For<IBus>();
        CaptureCreated? published = null;
        await bus.Publish(Arg.Do<CaptureCreated>(e => published = e), Arg.Any<CancellationToken>());
        bus.ClearReceivedCalls();

        await CaptureRetryEndpoint.RetryAsync(
            capture.Id, captures, updates, bus, new DefaultHttpContext(), CancellationToken.None);

        published.Should().NotBeNull();
        return published!;
    }

    private static Capture OrphanVoiceAwaitingTranscript() => new(
        Guid.NewGuid(), ChannelKind.Telegram, VoiceCapture.PlaceholderContent, DateTimeOffset.UtcNow,
        LifecycleStage.Orphan, MatchedSkill: null, FailureReason: "the recording could not be transcribed");

    [Fact]
    public async Task Retry_VoiceCaptureStillAwaitingTranscript_RepublishesWithNeedsTranscription()
    {
        // Same bug class as #34, for the second flag: without this the retry hands the
        // literal placeholder to the classifier instead of re-attempting transcription.
        var capture = OrphanVoiceAwaitingTranscript();
        var update = new TelegramUpdate(1L, 55L, 4, capture.Id, DateTimeOffset.UtcNow, FileId: "voice-abc");

        var published = await RetryAndCaptureEventAsync(capture, update);

        published.NeedsTranscription.Should().BeTrue();
    }

    [Fact]
    public async Task Retry_VoiceCaptureThatAlreadyHasATranscript_DoesNotRequestTranscriptionAgain()
    {
        // Once the transcript landed, a retry is an ordinary reclassification.
        var capture = OrphanVoiceAwaitingTranscript() with { Content = "buy milk on the way home" };
        var update = new TelegramUpdate(1L, 55L, 4, capture.Id, DateTimeOffset.UtcNow, FileId: "voice-abc");

        var published = await RetryAndCaptureEventAsync(capture, update);

        published.NeedsTranscription.Should().BeFalse();
    }

    [Fact]
    public async Task Retry_NonVoiceCapture_DoesNotRequestTranscription()
    {
        var published = await RetryAndCaptureEventAsync(OrphanWithoutAttachment());

        published.NeedsTranscription.Should().BeFalse();
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
