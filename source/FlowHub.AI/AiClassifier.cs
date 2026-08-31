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

    public AiClassifier(
        IChatClient chat,
        IClassifier keyword,
        ILogger<AiClassifier> log,
        ChatOptions options,
        IVikunjaProjectCatalog catalog,
        AiModelInfo modelInfo,
        IBridgeCatalog bridgeCatalog,
        bool allowBridgeClassification = false,
        RepoResolver? repoResolver = null)
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

            var response = await _chat.GetResponseAsync<AiClassificationResponse>(
                AiPrompts.BuildMessages(content, buckets, _allowBridgeClassification),
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

            var resolved = await TryResolveBridgeAsync(payload, content, sw, response, cancellationToken);
            if (resolved is not null)
            {
                return resolved;
            }

            var project = string.Equals(payload.MatchedSkill, "Vikunja", StringComparison.Ordinal)
                ? payload.Project
                : null;

            IReadOnlyDictionary<string, string>? entities = payload.Entities is { Count: > 0 }
                ? payload.Entities
                : null;

            sw.Stop();
            return new ClassificationResult(payload.Tags, payload.MatchedSkill, payload.Title, project, entities, BuildTrace(sw, response));
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
            BridgeAlias: resolution.Repo,
            BridgeAction: resolution.Action,
            BridgeBody: resolution.Body);
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
