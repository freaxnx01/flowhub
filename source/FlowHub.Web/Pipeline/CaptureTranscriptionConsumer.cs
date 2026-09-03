using FlowHub.Core.Captures;
using FlowHub.Core.Channels;
using FlowHub.Core.Events;
using FlowHub.Telegram;
using MassTransit;

namespace FlowHub.Web.Pipeline;

/// <summary>
/// Turns a voice Capture's audio into text. Runs off the band so the Telegram poll
/// loop is never blocked (design D3): on success it fills Content and re-publishes
/// CaptureCreated without the flag, so the ordinary classify → route path runs on
/// real text. On failure the Capture becomes an Orphan, which surfaces in Needs
/// attention and is retryable through the existing retry endpoint.
/// </summary>
public sealed partial class CaptureTranscriptionConsumer : IConsumer<CaptureCreated>
{
    private readonly ISpeechToText _speech;
    private readonly ITelegramGateway _gateway;
    private readonly ITelegramUpdateRepository _updates;
    private readonly ICaptureService _captures;
    private readonly ILogger<CaptureTranscriptionConsumer> _log;

    public CaptureTranscriptionConsumer(
        ISpeechToText speech,
        ITelegramGateway gateway,
        ITelegramUpdateRepository updates,
        ICaptureService captures,
        ILogger<CaptureTranscriptionConsumer> log)
    {
        _speech = speech;
        _gateway = gateway;
        _updates = updates;
        _captures = captures;
        _log = log;
    }

    public async Task Consume(ConsumeContext<CaptureCreated> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        if (!msg.NeedsTranscription)
        {
            return;
        }

        var update = await _updates.FindByCaptureIdAsync(msg.CaptureId, ct);
        if (update?.FileId is null)
        {
            await FailAsync(msg.CaptureId, "no Telegram file recorded for this capture", update, ct);
            return;
        }

        var audio = await _gateway.DownloadFileAsync(update.FileId, ct);
        if (audio is null)
        {
            await FailAsync(msg.CaptureId, "the audio could not be downloaded from Telegram", update, ct);
            return;
        }

        string? transcript;
        await using (audio)
        {
            transcript = await _speech.TranscribeAsync(audio, $"voice-{msg.CaptureId}.ogg", ct);
        }

        if (string.IsNullOrWhiteSpace(transcript))
        {
            await FailAsync(msg.CaptureId, "the recording could not be transcribed", update, ct);
            return;
        }

        LogTranscribed(msg.CaptureId, transcript.Length);

        // Persist before re-publishing so the row and the event agree. Without this the
        // Capture keeps its placeholder in the grids, the search filter and the
        // embedding, even though classification sees the real text (design D8).
        await _captures.SetTranscriptAsync(msg.CaptureId, transcript, ct);

        // Re-publishing CaptureCreated is how the pipeline is re-entered — the same
        // mechanism CaptureRetryEndpoint uses. Without the flag this time, so
        // enrichment classifies the transcript.
        await context.Publish(
            new CaptureCreated(msg.CaptureId, transcript, msg.Source, msg.CreatedAt), ct);
    }

    private async Task FailAsync(Guid captureId, string reason, TelegramUpdate? update, CancellationToken ct)
    {
        LogTranscriptionFailed(captureId, reason);
        await _captures.MarkOrphanAsync(captureId, reason, ct);

        if (update is not null)
        {
            // Best-effort: the operator should learn from the chat, not only the dashboard.
            try
            {
                await _gateway.SendTextAsync(update.ChatId, $"Sorry — {reason}.", ct);
            }
            catch (HttpRequestException ex)
            {
                LogReplyFailed(ex, captureId);
            }
        }
    }

    [LoggerMessage(EventId = 5110, Level = LogLevel.Information,
        Message = "Transcribed capture {CaptureId} ({Length} chars)")]
    private partial void LogTranscribed(Guid captureId, int length);

    [LoggerMessage(EventId = 5111, Level = LogLevel.Warning,
        Message = "Transcription failed for capture {CaptureId}: {Reason}")]
    private partial void LogTranscriptionFailed(Guid captureId, string reason);

    [LoggerMessage(EventId = 5112, Level = LogLevel.Warning,
        Message = "Could not send the transcription-failure reply for capture {CaptureId}")]
    private partial void LogReplyFailed(Exception ex, Guid captureId);
}
