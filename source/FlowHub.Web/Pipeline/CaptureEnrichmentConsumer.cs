using FlowHub.AI;
using FlowHub.Core.Captures;
using FlowHub.Core.Classification;
using FlowHub.Core.Events;
using MassTransit;

namespace FlowHub.Web.Pipeline;

/// <summary>
/// First stage of the async pipeline (ADR 0003): consumes <c>CaptureCreated</c>,
/// runs the capture through <c>IClassifier</c> (AI with deterministic keyword
/// fallback), and either advances it to <c>Classified</c> — publishing
/// <c>CaptureClassified</c> for the routing stage — or marks it <c>Orphan</c> when
/// no skill matches. Retries per the consumer's MassTransit policy; on exhaustion
/// the fault surfaces to <see cref="LifecycleFaultObserver"/>.
/// </summary>
public sealed partial class CaptureEnrichmentConsumer : IConsumer<CaptureCreated>
{
    private readonly IClassifier _classifier;
    private readonly EnricherDispatcher _enricher;
    private readonly ICaptureService _captureService;
    private readonly ILogger<CaptureEnrichmentConsumer> _logger;

    public CaptureEnrichmentConsumer(
        IClassifier classifier,
        EnricherDispatcher enricher,
        ICaptureService captureService,
        ILogger<CaptureEnrichmentConsumer> logger)
    {
        _classifier = classifier;
        _enricher = enricher;
        _captureService = captureService;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<CaptureCreated> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        if (msg.HasAttachment)
        {
            await _captureService.MarkClassifiedAsync(msg.CaptureId, "Paperless", cancellationToken: ct);
            await context.Publish(new CaptureClassified(
                msg.CaptureId,
                Tags: ["document"],
                MatchedSkill: "Paperless",
                ClassifiedAt: DateTimeOffset.UtcNow), ct);
            return;
        }

        var result = await _classifier.ClassifyAsync(msg.Content, ct);

        // Bridge with no determinable target → park for triage before any publish or
        // network call (spec decision #6). Two distinct causes, two distinct reasons:
        // no alias means the LLM proposed Bridge but no repo is known (issue #38);
        // an alias with an Unknown action means issue-vs-idea could not be decided.
        if (string.Equals(result.MatchedSkill, "Bridge", StringComparison.Ordinal)
            && result.BridgeAction == BridgeAction.Unknown)
        {
            await _captureService.MarkUnhandledAsync(msg.CaptureId, BridgeUndeterminedReason(result), ct);
            LogBridgeUndetermined(msg.CaptureId, result.BridgeAlias ?? string.Empty);
            return;
        }

        if (string.IsNullOrEmpty(result.MatchedSkill))
        {
            await _captureService.MarkOrphanAsync(msg.CaptureId, "no skill matched during classification", ct);
            LogOrphan(msg.CaptureId);
            return;
        }

        // Skip the DB round-trip + enricher dispatch for non-Vikunja captures —
        // dispatcher would early-return (null, null) anyway.
        string? project = null;
        EnrichmentResult? enrichment = null;
        if (string.Equals(result.MatchedSkill, "Vikunja", StringComparison.Ordinal))
        {
            var capture = await _captureService.GetByIdAsync(msg.CaptureId, ct)
                ?? throw new InvalidOperationException($"Capture {msg.CaptureId} not found in store.");

            (project, enrichment) = await _enricher.DispatchAsync(capture, result, ct);
        }

        await _captureService.MarkClassifiedAsync(
            msg.CaptureId,
            result.MatchedSkill,
            result.Title,
            project,
            enrichment?.Description,
            result.Trace,
            ct);

        await context.Publish(new CaptureClassified(
            msg.CaptureId,
            result.Tags,
            result.MatchedSkill,
            DateTimeOffset.UtcNow,
            project,
            enrichment?.Description,
            result.BridgeAlias,
            result.BridgeAction,
            result.BridgeBody));
    }

    private static string BridgeUndeterminedReason(ClassificationResult result) =>
        result.BridgeAlias is null
            ? "bridge candidate — repo undetermined"
            : "bridge action undetermined — needs triage";

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "Capture {CaptureId} classified as Orphan (no matched skill)")]
    private partial void LogOrphan(Guid captureId);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Information,
        Message = "Capture {CaptureId} bridge action undetermined (alias={Alias}) — marked Unhandled for triage")]
    private partial void LogBridgeUndetermined(Guid captureId, string alias);
}
