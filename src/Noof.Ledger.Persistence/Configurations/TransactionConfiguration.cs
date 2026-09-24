using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    public void Configure(EntityTypeBuilder<Transaction> builder)
    {
        builder.ToTable("transactions", table =>
        {
            table.HasCheckConstraint(
                "ck_transactions_capture_has_content",
                "(capture_kind IN (0, 2) AND raw_text IS NOT NULL) OR (capture_kind = 1 AND voice_file_id IS NOT NULL)");
            table.HasCheckConstraint(
                "ck_transactions_telegram_ids_match_capture_kind",
                "(capture_kind = 2 AND telegram_chat_id IS NULL AND telegram_message_id IS NULL) "
                + "OR (capture_kind <> 2 AND telegram_chat_id IS NOT NULL AND telegram_message_id IS NOT NULL)");
        });

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id).HasColumnName("id");
        builder.Property(t => t.WalletId).HasColumnName("wallet_id");
        builder.Property(t => t.Kind).HasColumnName("kind");
        builder.Property(t => t.RawText).HasColumnName("raw_text");
        builder.Property(t => t.CaptureKind).HasColumnName("capture_kind");
        builder.Property(t => t.VoiceFileId).HasColumnName("voice_file_id");
        builder.Property(t => t.VoiceDurationSeconds).HasColumnName("voice_duration_seconds");
        builder.Property(t => t.Status).HasColumnName("status");
        builder.Property(t => t.TimeZoneId).HasColumnName("time_zone_id").HasMaxLength(64).IsRequired();
        builder.Property(t => t.OccurredAt).HasColumnName("occurred_at");
        builder.Property(t => t.OccurredOn).HasColumnName("occurred_on");
        builder.Property(t => t.TelegramChatId).HasColumnName("telegram_chat_id");
        builder.Property(t => t.TelegramMessageId).HasColumnName("telegram_message_id");
        builder.Property(t => t.BotMessageId).HasColumnName("bot_message_id");
        builder.Property(t => t.PromptMessageId).HasColumnName("prompt_message_id");
        builder.Property(t => t.CreatedAt).HasColumnName("created_at");

        // The name stays EF's default: EfCaptureStore recognises a redelivered message by it.
        builder.HasIndex(t => new { t.TelegramChatId, t.TelegramMessageId })
            .IsUnique()
            .HasFilter("telegram_chat_id IS NOT NULL");

        builder.HasOne<Wallet>()
            .WithMany()
            .HasForeignKey(t => t.WalletId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
