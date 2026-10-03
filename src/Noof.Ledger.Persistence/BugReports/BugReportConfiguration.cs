using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.BugReports;

internal sealed class BugReportConfiguration : IEntityTypeConfiguration<BugReport>
{
    // One name for the index and for the catch that recognises a redelivered report by it.
    internal const string ReplyToIndex = "IX_bug_reports_source_reply_to";

    // The numbers are BugReportSource, BugReportStatus and BugExplanationState as spec R-1 numbers them; 0 is Unknown
    // and never stored. No constraint names a source's own value (spec R-2): a new source adds no schema change.
    public void Configure(EntityTypeBuilder<BugReport> builder)
    {
        builder.ToTable("bug_reports", table =>
        {
            table.HasCheckConstraint(
                "ck_bug_reports_source_and_reply_to", "source <> 0 AND (delivered_as IS NULL OR reply_to IS NOT NULL)");
            table.HasCheckConstraint(
                "ck_bug_reports_closed_at_matches_status", "status <> 0 AND (status = 2) = (closed_at IS NOT NULL)");
            table.HasCheckConstraint(
                "ck_bug_reports_explanation_matches_state",
                "explanation_state <> 0 AND (explanation_state = 2) = (explanation IS NOT NULL AND looks_like_bug IS NOT NULL)");
        });

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.Number).HasColumnName("number").UseIdentityAlwaysColumn();
        builder.Property(r => r.CreatedAt).HasColumnName("created_at");
        builder.Property(r => r.Source).HasColumnName("source");
        builder.Property(r => r.Text).HasColumnName("text");
        builder.Property(r => r.TransactionId).HasColumnName("transaction_id");
        builder.Property(r => r.ReplyTo).HasColumnName("reply_to");
        builder.Property(r => r.Status).HasColumnName("status");
        builder.Property(r => r.ClosedAt).HasColumnName("closed_at");
        builder.Property(r => r.SnapshotAt).HasColumnName("snapshot_at");
        builder.Property(r => r.RecordSummary).HasColumnName("record_summary");
        builder.Property(r => r.FindingsJson).HasColumnName("findings").HasColumnType("jsonb");
        builder.Property(r => r.LogLinesJson).HasColumnName("log_lines").HasColumnType("jsonb");
        builder.Property(r => r.CollectionFailures).HasColumnName("collection_failures");
        builder.Property(r => r.ExplanationState).HasColumnName("explanation_state");
        builder.Property(r => r.ExplanationAttempts).HasColumnName("explanation_attempts");
        builder.Property(r => r.ExplanationNextAt).HasColumnName("explanation_next_at");
        builder.Property(r => r.Explanation).HasColumnName("explanation");
        builder.Property(r => r.LooksLikeBug).HasColumnName("looks_like_bug");
        builder.Property(r => r.DeliveredAs).HasColumnName("delivered_as");

        builder.HasIndex(r => r.Number).IsUnique();

        builder.HasIndex(r => new { r.Source, r.ReplyTo })
            .IsUnique()
            .HasFilter("reply_to IS NOT NULL")
            .HasDatabaseName(ReplyToIndex);

        // RESTRICT: a report is evidence about its record, as a revision is.
        builder.HasOne<Transaction>()
            .WithMany()
            .HasForeignKey(r => r.TransactionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
