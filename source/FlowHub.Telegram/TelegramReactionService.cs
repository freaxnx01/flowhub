using FlowHub.Core.Captures;
using FlowHub.Core.Channels;
using Microsoft.Extensions.Logging;

namespace FlowHub.Telegram;

/// <summary>
/// Marks the operator's original Telegram message with the outcome of its Capture.
/// Telegram has no "mark as read" for bots, so this reaction is the only in-chat
/// signal that a message has been processed.
/// </summary>
public sealed partial class TelegramReactionService
{
    private readonly ITelegramUpdateRepository _updates;
    private readonly ITelegramGateway _gateway;
    private readonly ILogger<TelegramReactionService> _logger;

    public TelegramReactionService(
        ITelegramUpdateRepository updates,
        ITelegramGateway gateway,
        ILogger<TelegramReactionService> logger)
    {
        _updates = updates;
        _gateway = gateway;
        _logger = logger;
    }

    /// <summary>
    /// The emoji for a terminal stage, or null for a stage that is still in flight.
    /// Must come from ReactionTypeEmoji's fixed allow-list — ✅, ⚠️ and ❓ are NOT on it.
    /// </summary>
    public static string? EmojiFor(LifecycleStage stage) => stage switch
    {
        LifecycleStage.Completed => "👍",
        LifecycleStage.Orphan => "💔",
        LifecycleStage.Unhandled => "🤔",
        _ => null,
    };

    /// <summary>
    /// Applies the reaction for a resolved Capture. Idempotent and best-effort: an
    /// unknown Capture is a no-op, and a Telegram failure is logged, never thrown —
    /// a failed reaction must not fail the lifecycle transition that triggered it.
    /// </summary>
    public async Task ApplyAsync(Guid captureId, LifecycleStage stage, CancellationToken cancellationToken = default)
    {
        var emoji = EmojiFor(stage);
        if (emoji is null)
        {
            return;
        }

        try
        {
            var update = await _updates.FindByCaptureIdAsync(captureId, cancellationToken);
            if (update is null)
            {
                return;
            }

            await _gateway.SetReactionAsync(update.ChatId, update.MessageId, emoji, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            LogReactionFailed(ex, captureId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogReactionTimedOut(captureId);
        }
    }

    [LoggerMessage(EventId = 5010, Level = LogLevel.Warning,
        Message = "Could not set Telegram reaction (captureId={CaptureId})")]
    private partial void LogReactionFailed(Exception ex, Guid captureId);

    [LoggerMessage(EventId = 5011, Level = LogLevel.Warning,
        Message = "Timed out setting Telegram reaction (captureId={CaptureId})")]
    private partial void LogReactionTimedOut(Guid captureId);
}
