using FlowHub.Core.Captures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;

namespace FlowHub.Telegram;

/// <summary>Host wiring for the Telegram inbound Channel.</summary>
public static class TelegramServiceCollectionExtensions
{
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
        services.AddSingleton<ITelegramBotClient>(_ => new TelegramBotClient(options.BotToken!));
        services.AddScoped<ITelegramGateway, TelegramGateway>();
        services.AddScoped<TelegramReactionService>();
        services.AddScoped<TelegramUpdateHandler>();
        services.AddHostedService<TelegramPollingService>();

        // No Scrutor in this repo — decorate ICaptureService manually, matching the
        // lifetime EfCaptureService is already registered with (see
        // PersistenceServiceCollectionExtensions.AddFlowHubPersistence).
        var captureDescriptor = services.Last(d => d.ServiceType == typeof(ICaptureService));
        services.Add(ServiceDescriptor.Describe(
            typeof(ICaptureService),
            sp => new TelegramReactionCaptureServiceDecorator(
                (ICaptureService)ActivatorUtilities.CreateInstance(sp, captureDescriptor.ImplementationType!),
                sp.GetRequiredService<TelegramReactionService>()),
            captureDescriptor.Lifetime));

        return services;
    }
}
