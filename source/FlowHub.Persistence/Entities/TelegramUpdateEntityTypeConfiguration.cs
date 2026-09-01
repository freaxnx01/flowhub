using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowHub.Persistence.Entities;

internal sealed class TelegramUpdateEntityTypeConfiguration : IEntityTypeConfiguration<TelegramUpdateEntity>
{
    public void Configure(EntityTypeBuilder<TelegramUpdateEntity> builder)
    {
        builder.ToTable("TelegramUpdates");
        builder.HasKey(t => t.UpdateId);
        builder.Property(t => t.UpdateId).ValueGeneratedNever();
        builder.Property(t => t.FileId).HasMaxLength(256);
        builder.HasIndex(t => t.CaptureId);
        builder.HasIndex(t => t.ProcessedAt);
    }
}
