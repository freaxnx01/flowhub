using System.Diagnostics;
using FlowHub.Core.Classification;
using FlowHub.Core.Skills;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace FlowHub.AI;

/// <summary>
/// LLM-backed <see cref="IClassifier"/> (ADR 0004). Sends the capture to the
/// configured chat model, parses the schema-validated response, and re-validates
/// the matched skill against an allow-list. Any model error, timeout, or invalid
/// response degrades deterministically to the keyword classifier (logged under a
/// dedicated EventId), so a capture is always classified.
/// </summary>
internal sealed partial class AiClassifier : IClassifier
{
    private static readonly string[] SkillsWithoutBridge = ["Wallabag", "Vikunja", ""];
    private static readonly string[] SkillsWithBridge = ["Wallabag", "Vikunja", "Bridge", ""];

    private readonly IChatClient _chat;
    private readonly IClassifier _keyword;
    private readonly ILogger<AiClassifier> _log;
    private readonly ChatOptions _options;
    private readonly IVikunjaProjectCatalog _catalog;
    private readonly AiModelInfo _modelInfo;
    private readonly IBridgeCatalog _bridgeCatalog;
    private readonly bool _allowBridgeClassification;
    private readonly string[] _allowedSkills;
    private readonly RepoResolver? _repoResolver;
    private readonly IGlossary _glossary;

    public AiClassifier(
        IChatClient chat,
        IClassifier keyword,
        ILogger<AiClassifier> log,
        ChatOptions options,
        IVikunjaProjectCatalog catalog,
        AiModelInfo modelInfo,
        IBridgeCatalog bridgeCatalog,
        bool allowBridgeClassification = false,
        RepoResolver? repoResolver = null,
        IGlossary? glossary = null)
    {
        _chat = chat;
        _keyword = keyword;
        _log = log;
        _options = options;
        _catalog = catalog;
        _modelInfo = modelInfo;
        _bridgeCatalog = bridgeCatalog;
        _allowBridgeClassification = allowBridgeClassification;
        _allowedSkills = allowBridgeClassification ? SkillsWithBridge : SkillsWithoutBridge;
        _repoResolver = repoResolver;
        _glossary = glossary ?? new EmptyGlossary();
    }

    public async Task<ClassificationResult> ClassifyAsync(string content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var sw = Stopwatch.StartNew();

        try
        {
            var aliases = await _bridgeCatalog.GetAliasesAsync(cancellationToken);
            if (BridgeAliasMatcher.TryMatch(content, aliases, out var alias, out var remainder))
            {
                return await ClassifyBridgeAsync(alias, remainder, sw, cancellationToken);
            }

            var catalog = await _catalog.GetAsync(cancellationToken);
            var buckets = catalog.Keys.ToArray();

            var glossary = await _glossary.GetAsync(cancellationToken);
            var shorthand = ShorthandResolver.Resolve(content, glossary);

            // D5: an owner-qualified prefix names a repo — that is explicit operator intent.
            // Route directly to Bridge and skip both LLM classification and repo inference.
            // Gated on the Bridge feature flag: every other path checks _allowedSkills, and
            // a glossary entry must not become a way around a flag on a deployment with no
            // bridge wired up. Flag off → fall through; the prefix still supplies prompt
            // context and a "prefix" entity, it just cannot select the Bridge skill.
            if (shorthand.BridgeTarget is not null && _allowBridgeClassification)
            {
                return BuildOwnerQualifiedBridgeResult(shorthand, sw);
            }

            return await ClassifyWithModelAsync(content, buckets, shorthand, sw, cancellationToken);
        }
        catch (Exception ex)
        {
            sw.Stop();
            var reason = ex is InvalidOperationException && ex.Message == "schema_violation"
                ? "schema_violation"
                : ex.GetType().Name;
            LogFellBack(reason, sw.ElapsedMilliseconds);
            return await _keyword.ClassifyAsync(content, cancellationToken);
        }
    }

    private ClassificationResult BuildOwnerQualifiedBridgeResult(ResolvedShorthand shorthand, Stopwatch sw)
    {
        sw.Stop();
        return new ClassificationResult(
            Tags: ["bridge"],
            MatchedSkill: "Bridge",
            Title: null,
            Entities: shorthand.Entities.Count > 0 ? shorthand.Entities : null,
            Trace: new ClassifierTrace(
                ClassifierKind.Ai,
                (int)sw.ElapsedMilliseconds,
                _modelInfo.Provider,
                _modelInfo.Model,
                null,
                null),
            BridgeAction: BridgeAction.Unknown,
            BridgeTarget: shorthand.BridgeTarget,
            UnknownShorthand: shorthand.UnknownTokens.Count > 0 ? shorthand.UnknownTokens : null);
    }

    private async Task<ClassificationResult> ClassifyWithModelAsync(
        string content,
        string[] buckets,
        ResolvedShorthand shorthand,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        var response = await _chat.GetResponseAsync<AiClassificationResponse>(
            AiPrompts.BuildMessages(content, buckets, _allowBridgeClassification, shorthand.PromptContext),
            _options,
            cancellationToken: cancellationToken);

        if (!response.TryGetResult(out var payload))
        {
            throw new InvalidOperationException("schema_violation");
        }

        if (Array.IndexOf(_allowedSkills, payload.MatchedSkill) < 0)
        {
            throw new InvalidOperationException("schema_violation");
        }

        IReadOnlyList<string>? unknown = shorthand.UnknownTokens.Count > 0 ? shorthand.UnknownTokens : null;

        var resolved = await TryResolveBridgeAsync(payload, content, sw, response, cancellationToken);
        if (resolved is not null)
        {
            return resolved with { UnknownShorthand = unknown };
        }

        var project = string.Equals(payload.MatchedSkill, "Vikunja", StringComparison.Ordinal)
            ? payload.Project
            : null;

        var entities = MergeEntities(shorthand.Entities, payload.Entities);

        sw.Stop();
        return new ClassificationResult(
            payload.Tags,
            payload.MatchedSkill,
            payload.Title,
            project,
            entities,
            BuildTrace(sw, response),
            UnknownShorthand: unknown);
    }

    private async Task<ClassificationResult?> TryResolveBridgeAsync(
        AiClassificationResponse payload,
        string content,
        Stopwatch sw,
        ChatResponse<AiClassificationResponse> response,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(payload.MatchedSkill, "Bridge", StringComparison.Ordinal) || _repoResolver is null)
        {
            return null;
        }

        var resolution = await _repoResolver.ResolveAsync(content, cancellationToken);
        if (resolution is null)
        {
            // Unresolved → let the caller fall through, producing Bridge with a null alias,
            // which CaptureEnrichmentConsumer parks as "bridge candidate — repo undetermined".
            return null;
        }

        sw.Stop();
        return new ClassificationResult(
            payload.Tags,
            "Bridge",
            Title: resolution.Title ?? payload.Title,
            Trace: BuildTrace(sw, response),
            BridgeAction: resolution.Action,
            BridgeBody: resolution.Body,
            BridgeTarget: resolution.Repo);
    }

    private async Task<ClassificationResult> ClassifyBridgeAsync(
        string alias, string remainder, Stopwatch sw, CancellationToken cancellationToken)
    {
        var response = await _chat.GetResponseAsync<AiBridgeResponse>(
            AiPrompts.BuildBridgeMessages(remainder),
            _options,
            cancellationToken: cancellationToken);

        if (!response.TryGetResult(out var payload))
        {
            throw new InvalidOperationException("schema_violation");
        }

        var action = payload.Action switch
        {
            "issue" => BridgeAction.Issue,
            "idea" => BridgeAction.Idea,
            _ => BridgeAction.Unknown,
        };

        var tags = payload.Tags is { Length: > 0 } ? payload.Tags : ["bridge"];

        sw.Stop();
        return new ClassificationResult(
            tags,
            "Bridge",
            Title: payload.Title,
            Trace: BuildTrace(sw, response),
            BridgeAlias: alias,
            BridgeAction: action,
            BridgeBody: payload.Body);
    }

    private static Dictionary<string, string>? MergeEntities(
        IReadOnlyDictionary<string, string> resolved,
        IReadOnlyDictionary<string, string>? fromModel)
    {
        if (resolved.Count == 0 && fromModel is not { Count: > 0 })
        {
            return null;
        }

        var merged = new Dictionary<string, string>(resolved, StringComparer.Ordinal);
        if (fromModel is not null)
        {
            foreach (var (key, value) in fromModel)
            {
                merged[key] = value;
            }
        }

        return merged;
    }

    // Latency (ms) and token counts fit int for any real classify call; casts are intentional.
    private ClassifierTrace BuildTrace<T>(Stopwatch sw, ChatResponse<T> response) =>
        new(
            ClassifierKind.Ai,
            (int)sw.ElapsedMilliseconds,
            _modelInfo.Provider,
            _modelInfo.Model,
            (int?)response.Usage?.InputTokenCount,
            (int?)response.Usage?.OutputTokenCount);

    [LoggerMessage(
        EventId = 3010,
        Level = LogLevel.Warning,
        Message = "AiClassifier fell back to keyword classifier (reason={Reason}, duration_ms={DurationMs})")]
    private partial void LogFellBack(string reason, long durationMs);
}
