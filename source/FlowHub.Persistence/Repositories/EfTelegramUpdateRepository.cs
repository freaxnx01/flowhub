using FlowHub.Core.Channels;
using FlowHub.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlowHub.Persistence.Repositories;

internal sealed class EfTelegramUpdateRepository : ITelegramUpdateRepository
{
    private readonly FlowHubDbContext _db;

    public EfTelegramUpdateRepository(FlowHubDbContext db) => _db = db;

    public Task<bool> ExistsAsync(long updateId, CancellationToken cancellationToken = default) =>
        _db.TelegramUpdates.AsNoTracking().AnyAsync(t => t.UpdateId == updateId, cancellationToken);

    public async Task RecordAsync(TelegramUpdate update, CancellationToken cancellationToken = default)
    {
        if (await ExistsAsync(update.UpdateId, cancellationToken))
        {
            return;
        }

        _db.TelegramUpdates.Add(new TelegramUpdateEntity
        {
            UpdateId = update.UpdateId,
            ChatId = update.ChatId,
            MessageId = update.MessageId,
            CaptureId = update.CaptureId,
            ProcessedAt = update.ProcessedAt,
        });
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<TelegramUpdate?> FindByCaptureIdAsync(Guid captureId, CancellationToken cancellationToken = default)
    {
        var entity = await _db.TelegramUpdates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.CaptureId == captureId, cancellationToken);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task<long?> GetLastProcessedUpdateIdAsync(CancellationToken cancellationToken = default)
    {
        var entity = await _db.TelegramUpdates.AsNoTracking()
            .OrderByDescending(t => t.ProcessedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return entity?.UpdateId;
    }

    private static TelegramUpdate ToDomain(TelegramUpdateEntity e) => new(
        UpdateId: e.UpdateId,
        ChatId: e.ChatId,
        MessageId: e.MessageId,
        CaptureId: e.CaptureId,
        ProcessedAt: e.ProcessedAt);
}
