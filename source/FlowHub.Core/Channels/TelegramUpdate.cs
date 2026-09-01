namespace FlowHub.Core.Channels;

/// <summary>
/// A Telegram update FlowHub has already processed. Serves two purposes: the
/// <paramref name="UpdateId"/> is the idempotency key that makes a redelivered
/// update harmless, and the chat/message pair are the coordinates needed to react
/// to the original message once its Capture reaches a terminal stage.
/// </summary>
/// <param name="UpdateId">Telegram's update id — the dedup key.</param>
/// <param name="ChatId">Chat the message arrived in.</param>
/// <param name="MessageId">Message to react to.</param>
/// <param name="CaptureId">The Capture created, or null when the update was rejected or unsupported.</param>
/// <param name="ProcessedAt">When FlowHub finished handling it.</param>
/// <param name="FileId">
/// Telegram file id of the attached audio, when the update carried a voice or audio
/// memo the handler deferred to the transcription consumer; null otherwise. The
/// consumer downloads by this id, so it must survive across the poll-loop boundary.
/// </param>
public sealed record TelegramUpdate(
    long UpdateId,
    long ChatId,
    int MessageId,
    Guid? CaptureId,
    DateTimeOffset ProcessedAt,
    string? FileId = null);
