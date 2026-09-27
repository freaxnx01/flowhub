using FlowHub.Core.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowHub.Telegram;

/// <summary>
/// Handles a tap on one of <see cref="TelegramMenu"/>'s buttons. Every tap is recorded,
/// because the poll offset is restored from the last recorded update.
/// </summary>
public sealed partial class TelegramCallbackHandler
{
    private readonly ITelegramUpdateRepository _updates;
    private readonly ITelegramGateway _gateway;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramCallbackHandler> _logger;

    public TelegramCallbackHandler(
        ITelegramUpdateRepository updates,
        ITelegramGateway gateway,
        IOptions<TelegramOptions> options,
        ILogger<TelegramCallbackHandler> logger)
    {
        _updates = updates;
        _gateway = gateway;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Handles one tap. Safe to call twice with the same update id.</summary>
    public async Task HandleAsync(TelegramCallback callback, CancellationToken cancellationToken = default)
    {
        if (await _updates.ExistsAsync(callback.UpdateId, cancellationToken))
        {
            LogCallbackAlreadyProcessed(callback.UpdateId);
            return;
        }

        if (!_options.IsAllowed(callback.FromUserId))
        {
            // Same stance as for messages: recorded so it is not redelivered, never answered.
            LogCallbackRejectedUnlistedUser(callback.UpdateId, callback.FromUserId);
            await RecordAsync(callback, cancellationToken);
            return;
        }

        await _gateway.AnswerCallbackAsync(callback.CallbackQueryId, cancellationToken);
        if (callback.Data == TelegramMenu.LegendCallbackData)
        {
            await _gateway.SendTextAsync(callback.ChatId,
                string.Join('\n', TelegramReactionService.Legend), cancellationToken);
        }

        await RecordAsync(callback, cancellationToken);
    }

    private Task RecordAsync(TelegramCallback callback, CancellationToken cancellationToken) =>
        _updates.RecordAsync(
            new TelegramUpdate(callback.UpdateId, callback.ChatId, callback.MessageId, CaptureId: null, DateTimeOffset.UtcNow),
            cancellationToken);

    [LoggerMessage(EventId = 5040, Level = LogLevel.Debug,
        Message = "Telegram callback already processed, skipping (updateId={UpdateId})")]
    private partial void LogCallbackAlreadyProcessed(long updateId);

    [LoggerMessage(EventId = 5041, Level = LogLevel.Warning,
        Message = "Rejected Telegram callback from unlisted user (updateId={UpdateId}, userId={UserId})")]
    private partial void LogCallbackRejectedUnlistedUser(long updateId, long userId);
}
