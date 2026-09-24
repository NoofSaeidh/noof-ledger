using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class CategorizationJobConfiguration : IEntityTypeConfiguration<CategorizationJob>
{
    // One name for the index and for every catch that recognises a redelivered reply by it.
    internal const string SourceMessageIndex = "IX_categorization_jobs_transaction_id_source_message_id_kind";

    public void Configure(EntityTypeBuilder<CategorizationJob> builder)
    {
        builder.ToTable("categorization_jobs", table =>
        {
            table.HasCheckConstraint(
                "ck_categorization_jobs_correction_has_instruction", "kind <> 1 OR instruction IS NOT NULL");
            table.HasCheckConstraint(
                "ck_categorization_jobs_transcription_has_voice_file", "kind <> 3 OR voice_file_id IS NOT NULL");
        });

        builder.HasKey(j => j.Id);

        builder.Property(j => j.Id).HasColumnName("id");
        builder.Property(j => j.TransactionId).HasColumnName("transaction_id");
        builder.Property(j => j.Status).HasColumnName("status");
        builder.Property(j => j.AttemptCount).HasColumnName("attempt_count");
        builder.Property(j => j.RunAfter).HasColumnName("run_after");
        builder.Property(j => j.ClaimedAt).HasColumnName("claimed_at");
        builder.Property(j => j.ClaimedBy).HasColumnName("claimed_by").HasMaxLength(128);
        builder.Property(j => j.LastError).HasColumnName("last_error");
        builder.Property(j => j.CreatedAt).HasColumnName("created_at");
        builder.Property(j => j.UpdatedAt).HasColumnName("updated_at");
        builder.Property(j => j.Kind).HasColumnName("kind");
        builder.Property(j => j.Instruction).HasColumnName("instruction");
        builder.Property(j => j.SourceMessageId).HasColumnName("source_message_id");
        builder.Property(j => j.InstructionDay).HasColumnName("instruction_day");
        builder.Property(j => j.VoiceFileId).HasColumnName("voice_file_id");

        builder.HasIndex(j => new { j.Status, j.RunAfter });

        builder.HasIndex(j => new { j.TransactionId, j.SourceMessageId, j.Kind })
            .IsUnique()
            .HasFilter("source_message_id IS NOT NULL")
            .HasDatabaseName(SourceMessageIndex);

        builder.HasOne<Transaction>()
            .WithMany()
            .HasForeignKey(j => j.TransactionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
