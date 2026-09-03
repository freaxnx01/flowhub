using FlowHub.AI;
using FlowHub.Core.Captures;
using FlowHub.Core.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowHub.Telegram;

/// <summary>
/// Turns one inbound Telegram message into a Capture. Order is deliberate: submit
/// first, record second, so a crash replays the update rather than losing the
/// Capture — the recorded update id makes the replay harmless.
/// </summary>
public sealed partial class TelegramUpdateHandler
{
    private readonly ICaptureService _captures;
    private readonly ITelegramUpdateRepository _updates;
    private readonly ITelegramGateway _gateway;
    private readonly TelegramReactionService _reactions;
    private readonly IUploadPolicy _uploads;
    private readonly TelegramOptions _options;
    private readonly SpeechOptions _speech;
    private readonly ILogger<TelegramUpdateHandler> _logger;

    public TelegramUpdateHandler(
        ICaptureService captures,
        ITelegramUpdateRepository updates,
        ITelegramGateway gateway,
        TelegramReactionService reactions,
        IUploadPolicy uploads,
        IOptions<TelegramOptions> options,
        IOptions<SpeechOptions> speech,
        ILogger<TelegramUpdateHandler> logger)
    {
        _captures = captures;
        _updates = updates;
        _gateway = gateway;
        _reactions = reactions;
        _uploads = uploads;
        _options = options.Value;
        _speech = speech.Value;
        _logger = logger;
    }

    /// <summary>Handles one message. Safe to call twice with the same update id.</summary>
    public async Task HandleAsync(TelegramMessage message, CancellationToken cancellationToken = default)
    {
        if (await _updates.ExistsAsync(message.UpdateId, cancellationToken))
        {
            LogUpdateAlreadyProcessed(message.UpdateId);
            return;
        }

        if (!_options.IsAllowed(message.FromUserId))
        {
            // Recorded but not answered: acking stops redelivery without confirming
            // the bot exists to a stranger. The body is deliberately not logged.
            LogUpdateRejectedUnlistedUser(message.UpdateId, message.FromUserId);
            await RecordAsync(message, captureId: null, cancellationToken);
            return;
        }

        if (message.File is { DurationSeconds: > 0 } audio)
        {
            await HandleAudioAsync(message, audio, cancellationToken);
            return;
        }

        if (message.File is not null)
        {
            await HandleFileAsync(message, message.File, cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(message.Text))
        {
            await _gateway.SendTextAsync(message.ChatId,
                "That message type is not supported yet — send text, a photo, or a document.",
                cancellationToken);
            await RecordAsync(message, captureId: null, cancellationToken);
            return;
        }

        var capture = await _captures.SubmitAsync(message.Text, ChannelKind.Telegram, null, cancellationToken: cancellationToken);
        await RecordAsync(message, capture.Id, cancellationToken);
        await ReactIfAlreadyResolvedAsync(capture.Id, cancellationToken);
    }

    private async Task HandleAudioAsync(TelegramMessage message, TelegramFile audio, CancellationToken cancellationToken)
    {
        if (audio.DurationSeconds > _speech.MaxSeconds)
        {
            await _gateway.SendTextAsync(message.ChatId,
                $"That recording is too long — the limit is {_speech.MaxSeconds} seconds.",
                cancellationToken);
            await RecordAsync(message, captureId: null, cancellationToken);
            return;
        }

        // Duration is not a proxy for size — a short high-bitrate recording can still be
        // large, and it would otherwise be queued for a download with no size guard at
        // all. Reuses the upload policy's cap so audio is bounded the same way documents
        // and photos already are in HandleFileAsync.
        if (audio.SizeBytes > _uploads.MaxBytes)
        {
            await _gateway.SendTextAsync(message.ChatId,
                $"That recording is too large — the limit is {_uploads.MaxBytes} bytes.",
                cancellationToken);
            await RecordAsync(message, captureId: null, cancellationToken);
            return;
        }

        if (!_speech.IsConfigured)
        {
            await _gateway.SendTextAsync(message.ChatId,
                "Voice messages are not supported yet — send text, a photo, or a document.",
                cancellationToken);
            await RecordAsync(message, captureId: null, cancellationToken);
            return;
        }

        // Submitted without the audio: the transcription consumer fetches it. The
        // handler must not download here — the poll loop is single-threaded (D3).
        var voiceCapture = await _captures.SubmitAsync(
            VoiceCapture.PlaceholderContent, ChannelKind.Telegram, attachment: null,
            needsTranscription: true, cancellationToken);
        await RecordAsync(message, voiceCapture.Id, cancellationToken);
    }

    private async Task HandleFileAsync(TelegramMessage message, TelegramFile file, CancellationToken cancellationToken)
    {
        if (!IsAcceptable(file, out var rejection))
        {
            await _gateway.SendTextAsync(message.ChatId, rejection, cancellationToken);
            await RecordAsync(message, captureId: null, cancellationToken);
            return;
        }

        var content = await _gateway.DownloadFileAsync(file.FileId, cancellationToken);
        if (content is null)
        {
            await _gateway.SendTextAsync(message.ChatId,
                "That file could not be downloaded from Telegram — try sending it again.", cancellationToken);
            await RecordAsync(message, captureId: null, cancellationToken);
            return;
        }

        await using (content)
        {
            var input = new AttachmentInput
            {
                Content = content,
                FileName = file.FileName,
                ContentType = file.ContentType,
                SizeBytes = file.SizeBytes,
            };

            var capture = await _captures.SubmitAsync(message.Text, ChannelKind.Telegram, input, cancellationToken: cancellationToken);
            await RecordAsync(message, capture.Id, cancellationToken);
            await ReactIfAlreadyResolvedAsync(capture.Id, cancellationToken);
        }
    }

    /// <summary>
    /// The pipeline can resolve a Capture before the update row exists, in which case the
    /// decorator's reaction found nothing to react to. Re-check once the row is written.
    /// </summary>
    private async Task ReactIfAlreadyResolvedAsync(Guid captureId, CancellationToken cancellationToken)
    {
        var current = await _captures.GetByIdAsync(captureId, cancellationToken);
        if (current is not null)
        {
            await _reactions.ApplyAsync(captureId, current.Stage, cancellationToken);
        }
    }

    private bool IsAcceptable(TelegramFile file, out string rejection)
    {
        if (file.SizeBytes > _uploads.MaxBytes)
        {
            rejection = $"That file is too large — the limit is {_uploads.MaxBytes} bytes.";
            return false;
        }

        if (!_uploads.AllowedContentTypes.Contains(file.ContentType))
        {
            rejection = $"{file.ContentType} is not an accepted file type — send a PDF, PNG, or JPEG.";
            return false;
        }

        rejection = "";
        return true;
    }

    private Task RecordAsync(TelegramMessage message, Guid? captureId, CancellationToken cancellationToken) =>
        _updates.RecordAsync(
            new TelegramUpdate(
                message.UpdateId, message.ChatId, message.MessageId, captureId, DateTimeOffset.UtcNow,
                FileId: message.File?.FileId),
            cancellationToken);

    [LoggerMessage(EventId = 5001, Level = LogLevel.Debug,
        Message = "Telegram update already processed, skipping (updateId={UpdateId})")]
    private partial void LogUpdateAlreadyProcessed(long updateId);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Warning,
        Message = "Rejected Telegram update from unlisted user (updateId={UpdateId}, userId={UserId})")]
    private partial void LogUpdateRejectedUnlistedUser(long updateId, long userId);
}
