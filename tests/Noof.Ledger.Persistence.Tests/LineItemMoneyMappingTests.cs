using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class LineItemMoneyMappingTests(PostgresFixture fixture)
{
    static Wallet NewWallet() => new()
        { Id = Guid.NewGuid(), Name = "Test wallet", Currency = CurrencyCode.Eur };

    static Transaction NewTransaction(Guid walletId) => new()
    {
        Id = Guid.NewGuid(),
        WalletId = walletId,
        RawText = "flat white 1234.5678",
        Status = TransactionStatus.Captured,
        TimeZoneId = "Europe/Belgrade",
        OccurredAt = DateTimeOffset.UtcNow,
        OccurredOn = DateOnly.FromDateTime(DateTime.UtcNow),
        TelegramChatId = 1,
        TelegramMessageId = 1,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task An_amount_round_trips_exactly_through_a_real_line_item()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var wallet = NewWallet();
        var transaction = NewTransaction(wallet.Id);
        db.Wallets.Add(wallet);
        db.Transactions.Add(transaction);
        db.LineItems.Add(new LineItem
        {
            Id = Guid.NewGuid(),
            TransactionId = transaction.Id,
            Description = "Flat white",
            Amount = new Money(1234.5678m, CurrencyCode.Eur),
            CategorizedBy = CategorizationAuthority.None,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var reloaded = await db.LineItems.SingleAsync(TestContext.Current.CancellationToken);

        reloaded.Amount.Amount.Should().Be(1234.5678m);
        reloaded.Amount.Currency.Should().Be(CurrencyCode.Eur);
    }

    [Fact]
    public async Task More_than_four_decimal_places_is_rounded_not_rejected()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var wallet = NewWallet();
        var transaction = NewTransaction(wallet.Id);
        db.Wallets.Add(wallet);
        db.Transactions.Add(transaction);
        db.LineItems.Add(new LineItem
        {
            Id = Guid.NewGuid(),
            TransactionId = transaction.Id,
            Description = "Rounded",
            Amount = new Money(1.00005m, CurrencyCode.Eur),
            CategorizedBy = CategorizationAuthority.None,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var reloaded = await db.LineItems.SingleAsync(TestContext.Current.CancellationToken);

        reloaded.Amount.Amount.Should().Be(1.0001m);
    }
}
