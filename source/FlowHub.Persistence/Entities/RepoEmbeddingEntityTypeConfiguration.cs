using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowHub.Persistence.Entities;

internal sealed class RepoEmbeddingEntityTypeConfiguration : IEntityTypeConfiguration<RepoEmbeddingEntity>
{
    public void Configure(EntityTypeBuilder<RepoEmbeddingEntity> builder)
    {
        builder.ToTable("RepoEmbeddings");
        builder.HasKey(r => r.RepoName);
        builder.Property(r => r.RepoName).HasMaxLength(256);
        builder.Property(r => r.ContentHash).HasMaxLength(64).IsRequired();
        builder.Property(r => r.UpdatedAt).IsRequired();

        // 384-dim, matching CaptureEntityTypeConfiguration — ADR 0006 governs any change.
        builder.Property(r => r.Embedding).HasColumnType("vector(384)").IsRequired(false);
    }
}
