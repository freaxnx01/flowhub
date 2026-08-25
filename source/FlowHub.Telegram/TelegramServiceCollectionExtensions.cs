using FlowHub.Core.Captures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;

namespace FlowHub.Telegram;

/// <summary>Host wiring for the Telegram inbound Channel.</summary>
public static class TelegramServiceCollectionExtensions
{
    /// <summary>Named <see cref="HttpClient"/> used by the Telegram bot client.</summary>
    public const string TelegramHttpClientName = "telegram";

    /// <summary>
    /// Registers the Channel only when <see cref="TelegramOptions.IsConfigured"/>. An
    /// unconfigured FlowHub — CI included — never contacts Telegram.
    /// </summary>
    public static IServiceCollection AddFlowHubTelegram(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(TelegramOptions.SectionName).Get<TelegramOptions>() ?? new TelegramOptions();
        if (!options.IsConfigured)
        {
            return services;
        }

        services.Configure<TelegramOptions>(configuration.GetSection(TelegramOptions.SectionName));
        // Via IHttpClientFactory, not `new TelegramBotClient(token)`: the client owns an
        // HttpClient for the process lifetime otherwise, which is the stale-DNS/socket
        // case CLAUDE.md's "always via IHttpClientFactory" rule exists to prevent.
        services.AddHttpClient(TelegramHttpClientName);
        services.AddSingleton<ITelegramBotClient>(sp => new TelegramBotClient(
            new TelegramBotClientOptions(options.BotToken!),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(TelegramHttpClientName)));
        services.AddScoped<ITelegramGateway, TelegramGateway>();
        services.AddScoped<TelegramReactionService>();
        services.AddScoped<TelegramUpdateHandler>();
        services.AddHostedService<TelegramPollingService>();

        // No Scrutor in this repo — decorate ICaptureService manually, matching the
        // lifetime EfCaptureService is already registered with (see
        // PersistenceServiceCollectionExtensions.AddFlowHubPersistence).
        var captureDescriptor = services.LastOrDefault(d => d.ServiceType == typeof(ICaptureService))
            ?? throw new InvalidOperationException(
                "AddFlowHubTelegram must run after ICaptureService is registered "
                + "(AddFlowHubPersistence) — it decorates that registration.");
        if (captureDescriptor.ImplementationType is null)
        {
            throw new InvalidOperationException(
                "ICaptureService is registered by factory or instance; the Telegram reaction "
                + "decorator can only wrap a type-based registration.");
        }

        services.Add(ServiceDescriptor.Describe(
            typeof(ICaptureService),
            sp => new TelegramReactionCaptureServiceDecorator(
                (ICaptureService)ActivatorUtilities.CreateInstance(sp, captureDescriptor.ImplementationType),
                sp.GetRequiredService<TelegramReactionService>()),
            captureDescriptor.Lifetime));

        return services;
    }
}
