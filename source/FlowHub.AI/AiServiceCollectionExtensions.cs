using Anthropic.SDK;
using FlowHub.AI.Enrichers;
using FlowHub.Core.Captures;
using FlowHub.Core.Classification;
using FlowHub.Core.Skills;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OpenAI;
using System.ClientModel;

namespace FlowHub.AI;

/// <summary>
/// Fallback project catalog used when Vikunja isn't configured: exposes the fallback
/// project plus every registered enricher's bucket name. This lets the classifier route
/// e.g. a quote to "Zitate" and the dispatcher invoke the matching enricher even without
/// a live Vikunja (the public demo relies on this). Ids are placeholders (-1) — only the
/// names matter for enrichment dispatch; real ids come from the live Vikunja catalog.
/// </summary>
internal sealed class EnricherBucketCatalog : IVikunjaProjectCatalog
{
    private readonly Task<IReadOnlyDictionary<string, int>> _buckets;

    public EnricherBucketCatalog(IEnumerable<IEnricher> enrichers, string fallbackName)
    {
        var buckets = new Dictionary<string, int>(StringComparer.Ordinal) { [fallbackName] = 0 };
        foreach (var enricher in enrichers)
        {
            buckets[enricher.BucketName] = -1;
        }
        _buckets = Task.FromResult<IReadOnlyDictionary<string, int>>(buckets);
    }

    public Task<IReadOnlyDictionary<string, int>> GetAsync(CancellationToken cancellationToken) => _buckets;
}

public static class AiServiceCollectionExtensions
{
    private const string DefaultAnthropicModel = "claude-haiku-4-5-20251001";
    private const string DefaultOpenRouterModel = "meta-llama/llama-3.1-70b-instruct";
    private const string DefaultOpenRouterEndpoint = "https://openrouter.ai/api/v1";

    public static IServiceCollection AddFlowHubAi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<KeywordClassifier>();

        // VikunjaFallback + EnricherDispatcher are always registered so the
        // dispatcher resolves cleanly even when AI / Vikunja are unconfigured.
        //   - VikunjaFallback reads Skills:Vikunja:Fallback* directly from
        //     IConfiguration. The same keys back VikunjaOptions used by
        //     VikunjaSkillIntegration, so both code paths see the same values
        //     at startup. (FlowHub.Skills does not reference FlowHub.AI, so
        //     VikunjaFallback can't be re-registered from AddVikunja today —
        //     when adding IOptionsMonitor-style reload support, move this record
        //     to FlowHub.Core.Skills and inject IOptions<VikunjaOptions> here.)
        //   - IVikunjaProjectCatalog: TryAddSingleton with an empty no-op catalog
        //     so DI validates when Skills:Vikunja isn't configured. AddVikunja's
        //     AddSingleton on the real VikunjaProjectCatalog overrides at resolve
        //     time — last AddSingleton wins. Must run before AddFlowHubSkills in
        //     Program.cs for this to hold.
        services.AddSingleton(_ =>
        {
            var section = configuration.GetSection("Skills:Vikunja");
            var fallbackName = section["FallbackProject"] ?? "Inbox";
            var fallbackId = int.TryParse(section["FallbackProjectId"], out var id) ? id : 0;
            return new VikunjaFallback(fallbackName, fallbackId);
        });
        services.TryAddSingleton<IVikunjaProjectCatalog>(sp =>
            new EnricherBucketCatalog(sp.GetServices<IEnricher>(), sp.GetRequiredService<VikunjaFallback>().Name));

        // IBridgeCatalog: TryAdd an empty no-op so the classifiers resolve even when the
        // Bridge skill isn't configured. AddBridge (FlowHub.Skills) AddSingletons the real
        // BridgeCatalog which overrides at resolve time — last AddSingleton wins. Must run
        // before AddFlowHubSkills in Program.cs (it already does: line 88 before line 93).
        services.TryAddSingleton<IBridgeCatalog>(_ => new EmptyBridgeCatalog());

        services.AddSingleton<EnricherDispatcher>();

        var pricingSection = configuration.GetSection(Pricing.ClassificationPricingOptions.SectionName);
        services.Configure<Pricing.ClassificationPricingOptions>(
            o => pricingSection.Bind(o));

        services.AddSingleton<IClassificationCostEstimator, Pricing.ClassificationCostEstimator>();

        var outcome = ResolveOutcome(configuration);
        services.AddSingleton(outcome);
        services.AddHostedService<AiBootLogger>();

        if (!outcome.UsesAi)
        {
            services.AddSingleton<IClassifier>(sp => sp.GetRequiredService<KeywordClassifier>());
            return services;
        }

        // ZitateEnricher needs IChatClient — only register when AI is configured.
        services.AddSingleton<IEnricher, ZitateEnricher>();

        var apiKey = configuration[$"Ai:{outcome.Provider}:ApiKey"]!;
        var model = outcome.Model!;
        var maxTokens = int.TryParse(configuration["Ai:MaxOutputTokens"], out var parsed) ? parsed : 300;
        var allowBridgeClassification =
            bool.TryParse(configuration["Ai:EnableBridgeClassification"], out var allowBridge) && allowBridge;

        services.AddSingleton<IChatClient>(sp =>
            BuildChatClient(outcome.Provider!.Value, apiKey, model, configuration)
                .AsBuilder()
                .UseOpenTelemetry(
                    loggerFactory: sp.GetService<ILoggerFactory>(),
                    sourceName: null,
                    configure: null)
                .Build());

        services.AddSingleton(new AiModelInfo(outcome.Provider!.Value.ToString(), model));

        // ScopedRepoEmbeddingStore is a singleton adapter that opens a DI scope
        // per-call to reach the scoped EfRepoEmbeddingStore. It is registered as a
        // concrete type (not as IRepoEmbeddingStore) so it does not shadow the
        // scoped store used elsewhere in the app.
        services.AddSingleton<ScopedRepoEmbeddingStore>();
        services.AddSingleton<RepoEmbeddingSynchronizer>(sp => new RepoEmbeddingSynchronizer(
            sp.GetRequiredService<IBridgeCatalog>(),
            sp.GetRequiredService<ScopedRepoEmbeddingStore>(),
            sp.GetRequiredService<IEmbeddingService>(),
            sp.GetRequiredService<ILogger<RepoEmbeddingSynchronizer>>()));
        services.AddSingleton<RepoResolver>(sp => new RepoResolver(
            sp.GetRequiredService<IChatClient>(),
            sp.GetRequiredService<IBridgeCatalog>(),
            sp.GetRequiredService<ScopedRepoEmbeddingStore>(),
            sp.GetRequiredService<IEmbeddingService>(),
            new ChatOptions { MaxOutputTokens = maxTokens, Temperature = 0.2f },
            sp.GetRequiredService<RepoEmbeddingSynchronizer>(),
            sp.GetRequiredService<ILogger<RepoResolver>>()));

        services.AddSingleton(sp => new AiClassifier(
            sp.GetRequiredService<IChatClient>(),
            sp.GetRequiredService<KeywordClassifier>(),
            sp.GetRequiredService<ILogger<AiClassifier>>(),
            new ChatOptions { MaxOutputTokens = maxTokens, Temperature = 0.2f },
            sp.GetRequiredService<IVikunjaProjectCatalog>(),
            sp.GetRequiredService<AiModelInfo>(),
            sp.GetRequiredService<IBridgeCatalog>(),
            allowBridgeClassification,
            allowBridgeClassification ? sp.GetRequiredService<RepoResolver>() : null));
        services.AddSingleton<IClassifier>(sp => sp.GetRequiredService<AiClassifier>());

        return services;
    }

    public static IServiceCollection AddFlowHubEmbeddings(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var baseUrl = configuration["Embeddings:BaseUrl"];
        var apiKey = configuration["Embeddings:ApiKey"];
        // Compose interpolation substitutes empty strings for unset vars, so an empty
        // value here means "not configured" — treat it the same as null.
        var model = configuration["Embeddings:Model"] is { Length: > 0 } m ? m : "mistral-embed";
        var timeoutSeconds = int.TryParse(configuration["Embeddings:TimeoutSeconds"], out var t) ? t : 10;
        int? dimensions = int.TryParse(configuration["Embeddings:Dimensions"], out var d) ? d : null;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return services;
        }

        var endpoint = new Uri(string.IsNullOrWhiteSpace(baseUrl)
            ? "https://api.mistral.ai/v1"
            : baseUrl);

        var openAiOptions = new OpenAIClientOptions
        {
            Endpoint = endpoint,
            NetworkTimeout = TimeSpan.FromSeconds(timeoutSeconds),
        };
        var embeddingClient = new OpenAIClient(
                new ApiKeyCredential(apiKey),
                openAiOptions)
            .GetEmbeddingClient(model)
            .AsIEmbeddingGenerator();

        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(embeddingClient);
        services.AddSingleton<IEmbeddingService>(sp => new AiEmbeddingService(
            sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
            sp.GetRequiredService<ILogger<AiEmbeddingService>>(),
            dimensions));

        return services;
    }

    /// <summary>
    /// Registers speech-to-text when <c>Speech:ApiKey</c> is set. The provider is
    /// whatever <c>Speech:BaseUrl</c> points at — a cloud endpoint or a local whisper
    /// server — because both implement /v1/audio/transcriptions (design D1).
    /// </summary>
    public static IServiceCollection AddFlowHubSpeech(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var speechSection = configuration.GetSection(SpeechOptions.SectionName);
        var options = speechSection.Get<SpeechOptions>() ?? new SpeechOptions();

        // Options are bound even when the feature is off, so MaxSeconds is always
        // resolvable — the handler's duration check must work with STT unconfigured.
        services.Configure<SpeechOptions>(o => speechSection.Bind(o));

        if (!options.IsConfigured)
        {
            return services;
        }

        var endpoint = new Uri(string.IsNullOrWhiteSpace(options.BaseUrl)
            ? "https://api.openai.com/v1"
            : options.BaseUrl);

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = endpoint,
            NetworkTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
        };
        var audioClient = new OpenAIClient(new ApiKeyCredential(options.ApiKey!), clientOptions)
            .GetAudioClient(options.Model);

        services.AddSingleton<ISpeechToText>(sp => new AiSpeechToText(
            audioClient, sp.GetRequiredService<ILogger<AiSpeechToText>>()));

        return services;
    }

    private static AiRegistrationOutcome ResolveOutcome(IConfiguration configuration)
    {
        var raw = configuration["Ai:Provider"];

        if (string.IsNullOrWhiteSpace(raw))
        {
            return new AiRegistrationOutcome(UsesAi: false, Provider: null, Model: null, Reason: "no-provider");
        }

        if (!Enum.TryParse<AiProvider>(raw, ignoreCase: true, out var provider))
        {
            throw new InvalidOperationException(
                $"Invalid Ai__Provider value: '{raw}'. Expected 'Anthropic' or 'OpenRouter'.");
        }

        var apiKey = configuration[$"Ai:{provider}:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new AiRegistrationOutcome(UsesAi: false, Provider: provider, Model: null, Reason: "missing-key");
        }

        var model = configuration[$"Ai:{provider}:Model"] is { Length: > 0 } m
            ? m
            : DefaultModelFor(provider);
        return new AiRegistrationOutcome(UsesAi: true, Provider: provider, Model: model, Reason: "configured");
    }

    private static string DefaultModelFor(AiProvider provider) => provider switch
    {
        AiProvider.Anthropic => DefaultAnthropicModel,
        AiProvider.OpenRouter => DefaultOpenRouterModel,
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    private static IChatClient BuildChatClient(
        AiProvider provider,
        string apiKey,
        string model,
        IConfiguration configuration)
    {
        var timeoutSeconds = int.TryParse(configuration["Ai:TimeoutSeconds"], out var parsed) ? parsed : 10;

        switch (provider)
        {
            case AiProvider.Anthropic:
                // MessagesEndpoint directly implements IChatClient in Anthropic.SDK 5.x.
                // The model is conveyed per-request via ChatOptions.ModelId, which we
                // set as a default via ConfigureOptions so callers need not repeat it.
                //
                // The HttpClient is created once (Singleton closure) to avoid socket exhaustion.
                // Note: we own this HttpClient instance — AnthropicClient does NOT dispose it.
                //
                // TODO Slice D: wire Anthropic cache_control: ephemeral on the system-prompt
                // segment for ~80% input-token discount after the second call.
                // Anthropic.SDK 5.x exposes PromptCacheType.AutomaticToolsAndSystem only via
                // the native MessageParameters API, not through the MEAI IChatClient bridge
                // (ChatOptions has no AdditionalProperties key the adapter currently honours).
                var anthropicHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
                return new AnthropicClient(apiKey, anthropicHttp).Messages
                    .AsBuilder()
                    .ConfigureOptions(o => o.ModelId = model)
                    .Build();

            case AiProvider.OpenRouter:
                var endpoint = new Uri(configuration["Ai:OpenRouter:Endpoint"] ?? DefaultOpenRouterEndpoint);
                // NetworkTimeout is on ClientPipelineOptions (base of OpenAIClientOptions) and
                // applies to the underlying HttpClient send — no custom HttpClient needed.
                var openAiOptions = new OpenAIClientOptions
                {
                    Endpoint = endpoint,
                    NetworkTimeout = TimeSpan.FromSeconds(timeoutSeconds),
                };
                return new OpenAIClient(new ApiKeyCredential(apiKey), openAiOptions)
                    .GetChatClient(model)
                    .AsIChatClient();

            default:
                throw new ArgumentOutOfRangeException(nameof(provider));
        }
    }
}
