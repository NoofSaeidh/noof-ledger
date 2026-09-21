using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Persistence.Secrets;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class AppSecretConfiguration : IEntityTypeConfiguration<AppSecret>
{
    public void Configure(EntityTypeBuilder<AppSecret> builder)
    {
        builder.ToTable("app_secret");

        builder.HasKey(s => s.Key);

        builder.Property(s => s.Key).HasColumnName("key");
        builder.Property(s => s.Ciphertext).HasColumnName("ciphertext").IsRequired();
        builder.Property(s => s.UpdatedAt).HasColumnName("updated_at");
    }
}
