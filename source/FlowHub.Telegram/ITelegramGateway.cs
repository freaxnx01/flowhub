namespace FlowHub.Telegram;

/// <summary>
/// The three Telegram operations FlowHub needs, behind a port so the handler can be
/// tested without a network. Implemented over Telegram.Bot in <c>TelegramGateway</c>.
/// </summary>
public interface ITelegramGateway
{
    /// <summary>Sends a plain-text reply into a chat.</summary>
    Task SendTextAsync(long chatId, string text, CancellationToken cancellationToken = default);

    /// <summary>Sets the single bot reaction on a message, replacing any previous one.</summary>
    Task SetReactionAsync(long chatId, int messageId, string emoji, CancellationToken cancellationToken = default);

    /// <summary>Downloads a file by id, or null when it cannot be fetched.</summary>
    Task<Stream?> DownloadFileAsync(string fileId, CancellationToken cancellationToken = default);
}

/// <summary>A file attached to an inbound message.</summary>
/// <param name="FileId">Telegram file id, used for download.</param>
/// <param name="FileName">Best available name for the file.</param>
/// <param name="ContentType">MIME type as reported by Telegram, or inferred for photos.</param>
/// <param name="SizeBytes">Size reported by Telegram.</param>
public sealed record TelegramFile(string FileId, string FileName, string ContentType, long SizeBytes);

/// <summary>An inbound message, mapped off Telegram.Bot's types at the edge.</summary>
/// <param name="UpdateId">Telegram update id — the dedup key.</param>
/// <param name="ChatId">Chat the message arrived in.</param>
/// <param name="MessageId">The message itself, for reactions.</param>
/// <param name="FromUserId">Sender, checked against the allow-list.</param>
/// <param name="Text">Message text or caption, when present.</param>
/// <param name="File">Attached photo or document, when present.</param>
public sealed record TelegramMessage(
    long UpdateId,
    long ChatId,
    int MessageId,
    long FromUserId,
    string? Text,
    TelegramFile? File);
