namespace FlowHub.Core.Channels;

/// <summary>Driven port for the Telegram Channel's own idempotency state.</summary>
public interface ITelegramUpdateRepository
{
    /// <summary>True when this update has already been processed.</summary>
    Task<bool> ExistsAsync(long updateId, CancellationToken cancellationToken = default);

    /// <summary>Records an update as processed. Idempotent — a duplicate id is ignored.</summary>
    Task RecordAsync(TelegramUpdate update, CancellationToken cancellationToken = default);

    /// <summary>Finds the update that produced a Capture, or null.</summary>
    Task<TelegramUpdate?> FindByCaptureIdAsync(Guid captureId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The most recently processed update id, by <see cref="TelegramUpdate.ProcessedAt"/> —
    /// deliberately NOT <c>MAX(UpdateId)</c>. After a week of inactivity Telegram picks the
    /// next update id at random rather than sequentially, so the maximum is not a safe
    /// high-water mark.
    /// </summary>
    Task<long?> GetLastProcessedUpdateIdAsync(CancellationToken cancellationToken = default);
}
