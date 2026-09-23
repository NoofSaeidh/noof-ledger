using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    public void Configure(EntityTypeBuilder<Transaction> builder)
    {
        builder.ToTable("transactions");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id).HasColumnName("id");
        builder.Property(t => t.WalletId).HasColumnName("wallet_id");
        builder.Property(t => t.RawText).HasColumnName("raw_text").IsRequired();
        builder.Property(t => t.Status).HasColumnName("status");
        builder.Property(t => t.TimeZoneId).HasColumnName("time_zone_id").HasMaxLength(64).IsRequired();
        builder.Property(t => t.OccurredAt).HasColumnName("occurred_at");
        builder.Property(t => t.OccurredOn).HasColumnName("occurred_on");
        builder.Property(t => t.TelegramChatId).HasColumnName("telegram_chat_id");
        builder.Property(t => t.TelegramMessageId).HasColumnName("telegram_message_id");
        builder.Property(t => t.BotMessageId).HasColumnName("bot_message_id");
        builder.Property(t => t.PromptMessageId).HasColumnName("prompt_message_id");
        builder.Property(t => t.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(t => new { t.TelegramChatId, t.TelegramMessageId }).IsUnique();

        builder.HasOne<Wallet>()
            .WithMany()
            .HasForeignKey(t => t.WalletId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
