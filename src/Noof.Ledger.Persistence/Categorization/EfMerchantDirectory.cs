using Microsoft.EntityFrameworkCore;
using Npgsql;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Categorization;

public sealed class EfMerchantDirectory(LedgerDbContext db, TimeProvider timeProvider) : IMerchantDirectory
{
    public async Task<IReadOnlyList<MerchantAliasEntry>> AliasesAsync(CancellationToken cancellationToken) =>
        await (
            from a in db.MerchantAliases.AsNoTracking()
            join m in db.Merchants.AsNoTracking() on a.MerchantId equals m.Id
            select new MerchantAliasEntry(a.Folded, a.MerchantId, m.DisplayName))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<MerchantOption>> MerchantsAsync(CancellationToken cancellationToken) =>
        await db.Merchants.AsNoTracking()
            .Select(m => new MerchantOption(m.Id, m.DisplayName))
            .ToListAsync(cancellationToken);

    public async Task<Guid> LinkAliasAsync(string folded, string displayName, CancellationToken cancellationToken)
    {
        RequireMaxLength(folded, 256, nameof(folded));
        RequireMaxLength(displayName, 256, nameof(displayName));

        var merchant = new Merchant
        {
            Id = Guid.NewGuid(),
            DisplayName = displayName,
            Kind = MerchantKind.Retail,
        };
        var alias = new MerchantAlias
        {
            Folded = folded,
            MerchantId = merchant.Id,
            CreatedAt = timeProvider.GetUtcNow(),
        };

        db.Merchants.Add(merchant);
        db.MerchantAliases.Add(alias);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return merchant.Id;
        }
        catch (DbUpdateException ex) when (IsDuplicateAliasViolation(ex))
        {
            // Both rows were staged in this same SaveChangesAsync call, which EF wraps in one
            // implicit transaction - the alias's primary-key violation rolled the merchant insert
            // back with it. There is no orphaned merchant row to clean up; the loser wrote nothing.
            db.Entry(merchant).State = EntityState.Detached;
            db.Entry(alias).State = EntityState.Detached;

            var winner = await db.MerchantAliases.AsNoTracking()
                .SingleAsync(a => a.Folded == folded, cancellationToken);
            return winner.MerchantId;
        }
    }

    static bool IsDuplicateAliasViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "PK_merchant_aliases",
        };

    static void RequireMaxLength(string value, int maxLength, string paramName)
    {
        if (value.Length > maxLength)
            throw new ArgumentException($"must be at most {maxLength} characters, but was {value.Length}.", paramName);
    }
}
