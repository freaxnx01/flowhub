using FlowHub.Core.Captures;
using FlowHub.Core.Classification;

namespace FlowHub.Telegram;

/// <summary>
/// Wraps <see cref="ICaptureService"/> so a Capture reaching a terminal stage marks its
/// originating Telegram message. Registered only when the Telegram Channel is
/// configured, so it is absent otherwise. Every non-terminal member delegates untouched.
/// </summary>
public sealed class TelegramReactionCaptureServiceDecorator : ICaptureService
{
    private readonly ICaptureService _inner;
    private readonly TelegramReactionService _reactions;

    public TelegramReactionCaptureServiceDecorator(ICaptureService inner, TelegramReactionService reactions)
    {
        _inner = inner;
        _reactions = reactions;
    }

    public Task<Capture?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _inner.GetByIdAsync(id, cancellationToken);

    public Task<IReadOnlyList<Capture>> GetAllAsync(CancellationToken cancellationToken = default) =>
        _inner.GetAllAsync(cancellationToken);

    public Task<IReadOnlyList<Capture>> GetRecentAsync(int count, CancellationToken cancellationToken = default) =>
        _inner.GetRecentAsync(count, cancellationToken);

    public Task<FailureCounts> GetFailureCountsAsync(CancellationToken cancellationToken = default) =>
        _inner.GetFailureCountsAsync(cancellationToken);

    public Task<Capture> SubmitAsync(string content, ChannelKind source, CancellationToken cancellationToken = default) =>
        _inner.SubmitAsync(content, source, cancellationToken);

    public Task<Capture> SubmitAsync(string? caption, ChannelKind source, AttachmentInput? attachment, CancellationToken cancellationToken = default) =>
        _inner.SubmitAsync(caption, source, attachment, cancellationToken);

    public Task MarkClassifiedAsync(Guid id, string matchedSkill, string? title = null, string? vikunjaProject = null, string? enrichmentDescription = null, ClassifierTrace? trace = null, CancellationToken cancellationToken = default) =>
        _inner.MarkClassifiedAsync(id, matchedSkill, title, vikunjaProject, enrichmentDescription, trace, cancellationToken);

    public Task MarkRoutedAsync(Guid id, CancellationToken cancellationToken = default) =>
        _inner.MarkRoutedAsync(id, cancellationToken);

    public async Task MarkCompletedAsync(Guid id, string? externalRef, CancellationToken cancellationToken = default)
    {
        await _inner.MarkCompletedAsync(id, externalRef, cancellationToken);
        await _reactions.ApplyAsync(id, LifecycleStage.Completed, cancellationToken);
    }

    public async Task MarkOrphanAsync(Guid id, string reason, CancellationToken cancellationToken = default)
    {
        await _inner.MarkOrphanAsync(id, reason, cancellationToken);
        await _reactions.ApplyAsync(id, LifecycleStage.Orphan, cancellationToken);
    }

    public async Task MarkUnhandledAsync(Guid id, string reason, CancellationToken cancellationToken = default)
    {
        await _inner.MarkUnhandledAsync(id, reason, cancellationToken);
        await _reactions.ApplyAsync(id, LifecycleStage.Unhandled, cancellationToken);
    }

    public Task<CapturePage> ListAsync(CaptureFilter filter, CancellationToken cancellationToken = default) =>
        _inner.ListAsync(filter, cancellationToken);

    public Task ResetForRetryAsync(Guid id, CancellationToken cancellationToken = default) =>
        _inner.ResetForRetryAsync(id, cancellationToken);
}
