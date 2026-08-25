using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace FlowHub.Telegram;

/// <summary>Telegram.Bot-backed <see cref="ITelegramGateway"/>.</summary>
public sealed partial class TelegramGateway : ITelegramGateway
{
    private readonly ITelegramBotClient _client;
    private readonly ILogger<TelegramGateway> _logger;

    public TelegramGateway(ITelegramBotClient client, ILogger<TelegramGateway> logger)
    {
        _client = client;
        _logger = logger;
    }

    public Task SendTextAsync(long chatId, string text, CancellationToken cancellationToken = default) =>
        _client.SendMessage(chatId, text, cancellationToken: cancellationToken);

    public Task SetReactionAsync(long chatId, int messageId, string emoji, CancellationToken cancellationToken = default) =>
        _client.SetMessageReaction(
            chatId,
            messageId,
            [new ReactionTypeEmoji { Emoji = emoji }],
            cancellationToken: cancellationToken);

    public async Task<Stream?> DownloadFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        try
        {
            var file = await _client.GetFile(fileId, cancellationToken);
            if (file.FilePath is null)
            {
                return null;
            }

            var buffer = new MemoryStream();
            await _client.DownloadFile(file.FilePath, buffer, cancellationToken);
            buffer.Position = 0;
            return buffer;
        }
        catch (HttpRequestException ex)
        {
            LogDownloadFailed(ex, fileId);
            return null;
        }
    }

    [LoggerMessage(EventId = 5020, Level = LogLevel.Warning,
        Message = "Could not download Telegram file (fileId={FileId})")]
    private partial void LogDownloadFailed(Exception ex, string fileId);
}
