namespace FlowHub.Persistence.Entities;

internal sealed class TelegramUpdateEntity
{
    public long UpdateId { get; set; }
    public long ChatId { get; set; }
    public int MessageId { get; set; }
    public Guid? CaptureId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
    public string? FileId { get; set; }
}
