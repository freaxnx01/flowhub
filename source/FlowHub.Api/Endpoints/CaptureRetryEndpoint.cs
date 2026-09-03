using FlowHub.Core.Captures;
using FlowHub.Core.Channels;
using FlowHub.Core.Events;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace FlowHub.Api.Endpoints;

internal static class CaptureRetryEndpoint
{
    private static readonly LifecycleStage[] RetryableStages = [LifecycleStage.Orphan, LifecycleStage.Unhandled];

    public static void MapCaptureRetryEndpoint(this RouteGroupBuilder captures)
    {
        captures.MapPost("/{id:guid}/retry", RetryAsync)
            .WithName("RetryCapture")
            .Produces<Capture>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    internal static async Task<Results<Accepted<Capture>, ProblemHttpResult>> RetryAsync(
        Guid id,
        ICaptureService captureService,
        ITelegramUpdateRepository telegramUpdates,
        IBus bus,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var capture = await captureService.GetByIdAsync(id, ct);
        if (capture is null)
        {
            return TypedResults.Problem(
                type: ProblemTypes.CaptureNotFound,
                title: "Capture not found.",
                detail: $"No capture exists with id {id}.",
                statusCode: StatusCodes.Status404NotFound,
                instance: httpContext.Request.Path);
        }

        if (!RetryableStages.Contains(capture.Stage))
        {
            return TypedResults.Problem(
                type: ProblemTypes.CaptureNotRetryable,
                title: "Capture stage is not retryable.",
                detail: $"Captures may only be retried from Orphan or Unhandled. Current stage: {capture.Stage}.",
                statusCode: StatusCodes.Status409Conflict,
                instance: httpContext.Request.Path);
        }

        await captureService.ResetForRetryAsync(id, ct);

        // Build the reset record directly rather than re-querying — avoids a race where the
        // in-memory MassTransit consumer has already classified the capture before we read it back.
        var reset = capture with { Stage = LifecycleStage.Raw, FailureReason = null };

        // Every flag on CaptureCreated must be re-derived here: each one defaults to
        // false, consumers branch on them, and a dropped flag silently changes how a
        // retried Capture is routed. HasAttachment was #34; NeedsTranscription is the
        // same bug class, found by review on #60.
        //
        // A voice Capture still carrying the placeholder has no transcript yet, so a
        // retry has to re-attempt transcription rather than hand the placeholder to the
        // classifier. The durable signal is the recorded Telegram file id — the audio
        // itself is never stored (design D5).
        var telegramUpdate = await telegramUpdates.FindByCaptureIdAsync(id, ct);
        var awaitingTranscript =
            telegramUpdate?.FileId is not null
            && string.Equals(capture.Content, VoiceCapture.PlaceholderContent, StringComparison.Ordinal);

        await bus.Publish(
            new CaptureCreated(
                capture.Id, capture.Content, capture.Source, capture.CreatedAt,
                HasAttachment: capture.Attachment is not null,
                NeedsTranscription: awaitingTranscript),
            ct);

        return TypedResults.Accepted($"/api/v1/captures/{id}", reset);
    }
}
