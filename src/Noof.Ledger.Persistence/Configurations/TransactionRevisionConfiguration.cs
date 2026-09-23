using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Revisions;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class TransactionRevisionConfiguration : IEntityTypeConfiguration<TransactionRevision>
{
    public void Configure(EntityTypeBuilder<TransactionRevision> builder)
    {
        builder.ToTable("transaction_revisions");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.TransactionId).HasColumnName("transaction_id");
        builder.Property(r => r.RevisionNumber).HasColumnName("revision_number");
        builder.Property(r => r.Kind).HasColumnName("kind");
        builder.Property(r => r.Instruction).HasColumnName("instruction");
        builder.Property(r => r.StatusBefore).HasColumnName("status_before");
        builder.Property(r => r.StatusAfter).HasColumnName("status_after");
        builder.Property(r => r.Snapshot).HasColumnName("snapshot").HasColumnType("jsonb");
        builder.Property(r => r.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(r => new { r.TransactionId, r.RevisionNumber }).IsUnique();

        builder.HasOne<Transaction>()
            .WithMany()
            .HasForeignKey(r => r.TransactionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
