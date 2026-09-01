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

    private static TelegramFile? MapFile(Message message) =>
        MapDocument(message)
        ?? MapVoice(message)
        ?? MapAudio(message)
        ?? MapPhoto(message);

    private static TelegramFile? MapDocument(Message message)
    {
        if (message.Document is not { } document)
        {
            return null;
        }

        return new TelegramFile(
            document.FileId,
            document.FileName ?? "document",
            document.MimeType ?? "application/octet-stream",
            document.FileSize ?? 0);
    }

    private static TelegramFile? MapVoice(Message message)
    {
        if (message.Voice is not { } voice)
        {
            return null;
        }

        return new TelegramFile(
            voice.FileId,
            $"voice-{message.MessageId}.ogg",
            voice.MimeType ?? "audio/ogg",
            voice.FileSize ?? 0,
            voice.Duration);
    }

    private static TelegramFile? MapAudio(Message message)
    {
        if (message.Audio is not { } audio)
        {
            return null;
        }

        return new TelegramFile(
            audio.FileId,
            audio.FileName ?? $"audio-{message.MessageId}.mp3",
            audio.MimeType ?? "audio/mpeg",
            audio.FileSize ?? 0,
            audio.Duration);
    }

    // Photos arrive as a size ladder; the last entry is the largest.
    private static TelegramFile? MapPhoto(Message message)
    {
        if (message.Photo is not { Length: > 0 } photos)
        {
            return null;
        }

        var largest = photos[^1];
        return new TelegramFile(largest.FileId, $"photo-{message.MessageId}.jpg", "image/jpeg", largest.FileSize ?? 0);
    }
}
