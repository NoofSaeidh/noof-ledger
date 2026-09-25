using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Persistence.Diagnostics;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class AppLogEntryConfiguration : IEntityTypeConfiguration<AppLogEntry>
{
    public void Configure(EntityTypeBuilder<AppLogEntry> builder)
    {
        builder.ToTable("app_log");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.LoggedAt).HasColumnName("logged_at");
        builder.Property(e => e.Level).HasColumnName("level").HasColumnType("smallint");
        builder.Property(e => e.Source).HasColumnName("source");
        builder.Property(e => e.Message).HasColumnName("message");
        builder.Property(e => e.Template).HasColumnName("template");
        builder.Property(e => e.Exception).HasColumnName("exception");
        builder.Property(e => e.TransactionId).HasColumnName("transaction_id");
        builder.Property(e => e.PropertiesJson).HasColumnName("properties").HasColumnType("jsonb");

        builder.HasIndex(e => e.LoggedAt);
        builder.HasIndex(e => e.TransactionId);
        builder.HasIndex(e => new { e.Level, e.LoggedAt });
    }
}
