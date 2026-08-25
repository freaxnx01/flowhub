using FlowHub.Core.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;

namespace FlowHub.Telegram;

/// <summary>
/// Long-polls getUpdates and feeds each message to <see cref="TelegramUpdateHandler"/>.
/// Outbound-only, so it needs no public ingress. The offset is restored from the last
/// processed update on start; failures back off instead of taking down the host.
/// </summary>
public sealed partial class TelegramPollingService : BackgroundService
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly ITelegramBotClient _client;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<TelegramPollingService> _logger;

    public TelegramPollingService(
        ITelegramBotClient client,
        IServiceScopeFactory scopes,
        ILogger<TelegramPollingService> logger)
    {
        _client = client;
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var offset = await RestoreOffsetAsync(stoppingToken);
        var backoff = MinBackoff;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await _client.GetUpdates(
                    offset: offset,
                    timeout: 50,
                    allowedUpdates: [UpdateType.Message],
                    cancellationToken: stoppingToken);

                foreach (var update in updates)
                {
                    offset = update.Id + 1;
                    var message = TelegramMessageMapper.Map(update);
                    if (message is null)
                    {
                        continue;
                    }

                    using var scope = _scopes.CreateScope();
                    var handler = scope.ServiceProvider.GetRequiredService<TelegramUpdateHandler>();
                    await handler.HandleAsync(message, stoppingToken);
                }

                backoff = MinBackoff;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 409)
            {
                // A webhook is registered, so getUpdates is refused for as long as it stands.
                // Backing off forever would look like silence; name the fix and stop.
                LogWebhookConflict(ex);
                return;
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 401)
            {
                LogUnauthorized(ex);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or ApiRequestException or InvalidOperationException)
            {
                LogPollFailed(ex, backoff);
                await Task.Delay(backoff, stoppingToken);
                backoff = backoff < MaxBackoff ? backoff * 2 : MaxBackoff;
            }
        }
    }

    private async Task<int> RestoreOffsetAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var updates = scope.ServiceProvider.GetRequiredService<ITelegramUpdateRepository>();
        var last = await updates.GetLastProcessedUpdateIdAsync(cancellationToken);
        return last is null ? 0 : (int)(last.Value + 1);
    }

    [LoggerMessage(EventId = 5030, Level = LogLevel.Critical,
        Message = "Telegram refuses getUpdates because a webhook is registered. "
            + "Call deleteWebhook for this bot, then restart FlowHub. Polling stopped.")]
    private partial void LogWebhookConflict(Exception ex);

    [LoggerMessage(EventId = 5031, Level = LogLevel.Critical,
        Message = "Telegram rejected the bot token as unauthorized. Check Telegram__BotToken. Polling stopped.")]
    private partial void LogUnauthorized(Exception ex);

    [LoggerMessage(EventId = 5032, Level = LogLevel.Error,
        Message = "Telegram poll failed; retrying (backoff={Backoff})")]
    private partial void LogPollFailed(Exception ex, TimeSpan backoff);
}
