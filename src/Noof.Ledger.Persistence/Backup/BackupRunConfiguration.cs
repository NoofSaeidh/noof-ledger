using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Noof.Ledger.Persistence.Backup;

internal sealed class BackupRunConfiguration : IEntityTypeConfiguration<BackupRun>
{
    public void Configure(EntityTypeBuilder<BackupRun> builder)
    {
        builder.ToTable("backup_runs");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.StartedAt).HasColumnName("started_at");
        builder.Property(r => r.FinishedAt).HasColumnName("finished_at");
        builder.Property(r => r.Succeeded).HasColumnName("succeeded");
        builder.Property(r => r.FileName).HasColumnName("file_name");
        builder.Property(r => r.SizeBytes).HasColumnName("size_bytes");
        builder.Property(r => r.Error).HasColumnName("error");
    }
}
