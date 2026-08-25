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
    private readonly IUploadPolicy _uploads;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramUpdateHandler> _logger;

    public TelegramUpdateHandler(
        ICaptureService captures,
        ITelegramUpdateRepository updates,
        ITelegramGateway gateway,
        IUploadPolicy uploads,
        IOptions<TelegramOptions> options,
        ILogger<TelegramUpdateHandler> logger)
    {
        _captures = captures;
        _updates = updates;
        _gateway = gateway;
        _uploads = uploads;
        _options = options.Value;
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

        var capture = await _captures.SubmitAsync(message.Text, ChannelKind.Telegram, null, cancellationToken);
        await RecordAsync(message, capture.Id, cancellationToken);
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
            if (!string.IsNullOrWhiteSpace(message.Text))
            {
                // Captures with an attachment take the filename as Content; the caption
                // is not persisted. Same limitation as the Web upload path.
                LogDroppingCaption(message.UpdateId);
            }

            var input = new AttachmentInput
            {
                Content = content,
                FileName = file.FileName,
                ContentType = file.ContentType,
                SizeBytes = file.SizeBytes,
            };

            var capture = await _captures.SubmitAsync(message.Text, ChannelKind.Telegram, input, cancellationToken);
            await RecordAsync(message, capture.Id, cancellationToken);
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
            new TelegramUpdate(message.UpdateId, message.ChatId, message.MessageId, captureId, DateTimeOffset.UtcNow),
            cancellationToken);

    [LoggerMessage(EventId = 5001, Level = LogLevel.Debug,
        Message = "Telegram update already processed, skipping (updateId={UpdateId})")]
    private partial void LogUpdateAlreadyProcessed(long updateId);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Warning,
        Message = "Rejected Telegram update from unlisted user (updateId={UpdateId}, userId={UserId})")]
    private partial void LogUpdateRejectedUnlistedUser(long updateId, long userId);

    [LoggerMessage(EventId = 5003, Level = LogLevel.Debug,
        Message = "Dropping caption on Telegram update (updateId={UpdateId})")]
    private partial void LogDroppingCaption(long updateId);
}
