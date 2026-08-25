using Telegram.Bot.Types;

namespace FlowHub.Telegram;

/// <summary>Maps Telegram.Bot's Update onto FlowHub's own <see cref="TelegramMessage"/>.</summary>
internal static class TelegramMessageMapper
{
    /// <summary>Returns null for updates FlowHub does not handle at all.</summary>
    public static TelegramMessage? Map(Update update)
    {
        var message = update.Message;
        if (message?.From is null)
        {
            return null;
        }

        return new TelegramMessage(
            UpdateId: update.Id,
            ChatId: message.Chat.Id,
            MessageId: message.MessageId,
            FromUserId: message.From.Id,
            Text: message.Text ?? message.Caption,
            File: MapFile(message));
    }

    private static TelegramFile? MapFile(Message message)
    {
        if (message.Document is { } document)
        {
            return new TelegramFile(
                document.FileId,
                document.FileName ?? "document",
                document.MimeType ?? "application/octet-stream",
                document.FileSize ?? 0);
        }

        // Photos arrive as a size ladder; the last entry is the largest.
        if (message.Photo is { Length: > 0 } photos)
        {
            var largest = photos[^1];
            return new TelegramFile(largest.FileId, $"photo-{message.MessageId}.jpg", "image/jpeg", largest.FileSize ?? 0);
        }

        return null;
    }
}
