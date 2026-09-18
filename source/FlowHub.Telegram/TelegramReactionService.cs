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
    /// Acknowledges receipt while a Capture is still in flight. Replaced by the outcome
    /// emoji when it resolves — a bot may hold only one reaction per message.
    /// </summary>
    public const string InFlightEmoji = "🫡";

    private const string CompletedFallbackEmoji = "👍";

    /// <summary>
    /// The emoji for a terminal stage, or null for a stage that is still in flight.
    /// Must come from ReactionTypeEmoji's fixed allow-list — ✅, ⚠️ and ❓ are NOT on it.
    /// </summary>
    /// <param name="stage">The Capture's lifecycle stage.</param>
    /// <param name="matchedSkill">
    /// The skill that handled it. Only <see cref="LifecycleStage.Completed"/> uses it:
    /// Telegram allows one reaction per message, so the success emoji is the only place
    /// the skill can be shown, and a Capture only has a skill once it completed.
    /// </param>
    public static string? EmojiFor(LifecycleStage stage, string? matchedSkill = null) => stage switch
    {
        LifecycleStage.Completed => EmojiForSkill(matchedSkill),
        LifecycleStage.Orphan => "💔",
        LifecycleStage.Unhandled => "🤔",
        // Speak-no-evil: the capture was classified and deliberately not sent
        // anywhere. Distinct from 💔/🤔, which both mean something went wrong.
        LifecycleStage.Withheld => "🙊",
        _ => null,
    };

    /// <summary>
    /// An unrecognised skill falls back rather than returning null: a skill added later
    /// must be wrong-but-visible, never silent.
    /// </summary>
    private static string EmojiForSkill(string? matchedSkill) => matchedSkill?.ToLowerInvariant() switch
    {
        "bridge" => "👨‍💻",
        "vikunja" => "✍",
        "wallabag" => "👀",
        // The allow-list has no paper, file or folder emoji; 👌 is chosen to read as
        // "filed, nothing left to do" and to be unmistakable beside the others.
        "paperless" => "👌",
        _ => CompletedFallbackEmoji,
    };

    /// <summary>
    /// Applies the reaction for a resolved Capture. Idempotent and best-effort: an
    /// unknown Capture is a no-op, and a Telegram failure is logged, never thrown —
    /// a failed reaction must not fail the lifecycle transition that triggered it.
    /// </summary>
    public async Task ApplyAsync(
        Guid captureId,
        LifecycleStage stage,
        string? matchedSkill = null,
        CancellationToken cancellationToken = default)
    {
        var emoji = EmojiFor(stage, matchedSkill);
        if (emoji is null)
        {
            return;
        }

        await SetAsync(captureId, emoji, cancellationToken);
    }

    /// <summary>
    /// Marks the message as received while its Capture is still in flight. Best-effort
    /// and non-throwing, exactly like <see cref="ApplyAsync"/>.
    /// </summary>
    public Task ApplyInFlightAsync(Guid captureId, CancellationToken cancellationToken = default) =>
        SetAsync(captureId, InFlightEmoji, cancellationToken);

    private async Task SetAsync(Guid captureId, string emoji, CancellationToken cancellationToken)
    {
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
