using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Categorization;
using Noof.Ledger.Persistence.Editing;
using Noof.Ledger.Persistence.Revisions;
using Npgsql;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class TransactionRevisionTests(PostgresFixture fixture)
{
    static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));
    static readonly Guid DefaultWalletId = new("00000000-0000-0000-0000-000000000001");
    static readonly Guid CoffeeCategoryId = new("00000000-0000-0000-0001-000000000017");

    static async Task<Guid> SeedTransactionAsync(LedgerDbContext db, TransactionKind kind = TransactionKind.Expense)
    {
        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            WalletId = DefaultWalletId,
            Kind = kind,
            RawText = "кофе 250",
            Status = TransactionStatus.Captured,
            TimeZoneId = "Europe/Belgrade",
            OccurredAt = new DateTimeOffset(2026, 9, 21, 10, 0, 0, TimeSpan.Zero),
            OccurredOn = new DateOnly(2026, 9, 21),
            TelegramChatId = 1,
            TelegramMessageId = 1,
            CreatedAt = new DateTimeOffset(2026, 9, 21, 10, 0, 0, TimeSpan.Zero),
        };
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return transaction.Id;
    }

    static CategorizedLineItem Coffee(decimal amount) => new("кофе", new Money(amount, CurrencyCode.Rsd), CoffeeCategoryId, null);

    [Fact]
    public async Task A_first_reading_appends_an_initial_revision_with_a_snapshot_of_what_was_written()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var transactionId = await SeedTransactionAsync(db);

        await new EfCategorizationStore(db, Clock).ApplyAsync(transactionId,
            new CategorizationOutcome([Coffee(250m)], new DateOnly(2026, 9, 21)), TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        var revision = await db.TransactionRevisions.SingleAsync(TestContext.Current.CancellationToken);
        revision.RevisionNumber.Should().Be(1);
        revision.Kind.Should().Be(RevisionKind.Initial);
        revision.StatusBefore.Should().Be(TransactionStatus.Captured);
        revision.StatusAfter.Should().Be(TransactionStatus.Completed);
        revision.Instruction.Should().BeNull();
        revision.CreatedAt.Should().Be(Clock.GetUtcNow());

        using var snapshot = JsonDocument.Parse(revision.Snapshot);
        snapshot.RootElement.GetProperty("raw_text").GetString().Should().Be("кофе 250");
        snapshot.RootElement.GetProperty("occurred_on").GetString().Should().Be("2026-09-21");
        var item = snapshot.RootElement.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("amount").ValueKind.Should().Be(JsonValueKind.String, "an amount is never a JSON number");
        decimal.Parse(item.GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture).Should().Be(250m);
        item.GetProperty("currency").GetString().Should().Be("RSD");
    }

    [Fact]
    public async Task A_correction_appends_the_next_revision_with_its_instruction()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var transactionId = await SeedTransactionAsync(db);
        var store = new EfCategorizationStore(db, Clock);

        await store.ApplyAsync(transactionId, new CategorizationOutcome([Coffee(250m)], new DateOnly(2026, 9, 21)), TestContext.Current.CancellationToken);
        await store.ApplyAsync(transactionId,
            new CategorizationOutcome([Coffee(1500m)], new DateOnly(2026, 9, 21), JobKind.Correct, "нет, 1500"),
            TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        var revisions = await db.TransactionRevisions.OrderBy(r => r.RevisionNumber).ToListAsync(TestContext.Current.CancellationToken);
        revisions.Select(r => (r.RevisionNumber, r.Kind)).Should().Equal((1, RevisionKind.Initial), (2, RevisionKind.Correction));
        revisions[1].Instruction.Should().Be("нет, 1500");
        revisions[1].StatusBefore.Should().Be(TransactionStatus.Completed);
    }

    [Fact]
    public async Task A_receipts_first_categorization_is_initial_but_a_later_correction_of_it_is_not()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var transactionId = await SeedTransactionAsync(db);
        var store = new EfCategorizationStore(db, Clock);

        await store.ApplyAsync(transactionId,
            new CategorizationOutcome([Coffee(250m)], new DateOnly(2026, 9, 21), JobKind.CategorizeReceipt), TestContext.Current.CancellationToken);
        await store.ApplyAsync(transactionId,
            new CategorizationOutcome([Coffee(250m)], new DateOnly(2026, 9, 21), JobKind.CategorizeReceipt, "wrong category, make it transport"),
            TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        var revisions = await db.TransactionRevisions.OrderBy(r => r.RevisionNumber).ToListAsync(TestContext.Current.CancellationToken);
        revisions.Select(r => (r.RevisionNumber, r.Kind)).Should().Equal((1, RevisionKind.Initial), (2, RevisionKind.Correction));
        revisions[1].Instruction.Should().Be("wrong category, make it transport");
    }

    [Fact]
    public async Task A_reinterpretation_is_recorded_as_an_edit()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var transactionId = await SeedTransactionAsync(db);

        await new EfCategorizationStore(db, Clock).ApplyAsync(transactionId,
            new CategorizationOutcome([Coffee(300m)], new DateOnly(2026, 9, 21), JobKind.Reinterpret), TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        (await db.TransactionRevisions.SingleAsync(TestContext.Current.CancellationToken)).Kind.Should().Be(RevisionKind.Edit);
    }

    [Theory]
    [InlineData("UPDATE public.transaction_revisions SET instruction = 'rewritten'")]
    [InlineData("DELETE FROM public.transaction_revisions")]
    [InlineData("TRUNCATE public.transaction_revisions")]
    public async Task The_history_cannot_be_rewritten(string sql)
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var transactionId = await SeedTransactionAsync(db);
        await new EfCategorizationStore(db, Clock).ApplyAsync(transactionId,
            new CategorizationOutcome([Coffee(250m)], new DateOnly(2026, 9, 21)), TestContext.Current.CancellationToken);

        var act = () => db.Database.ExecuteSqlRawAsync(sql, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("append-only");
    }

    [Fact]
    public async Task A_correction_applied_to_a_cancelled_record_keeps_it_cancelled()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var transactionId = await SeedTransactionAsync(db);
        var store = new EfCategorizationStore(db, Clock);
        await store.ApplyAsync(transactionId, new CategorizationOutcome([Coffee(250m)], new DateOnly(2026, 9, 21)), TestContext.Current.CancellationToken);
        await new EfRecordEditor(db, Clock).CancelAsync(transactionId, TestContext.Current.CancellationToken);

        await store.ApplyAsync(transactionId,
            new CategorizationOutcome([Coffee(1500m)], new DateOnly(2026, 9, 21), JobKind.Correct, "нет, 1500"),
            TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        (await db.Transactions.SingleAsync(t => t.Id == transactionId, TestContext.Current.CancellationToken))
            .Status.Should().Be(TransactionStatus.Cancelled, "only Restore brings a cancelled record back");
    }

    [Theory]
    [InlineData(TransactionKind.Expense, "Expense")]
    [InlineData(TransactionKind.Income, "Income")]
    public async Task A_snapshot_names_the_records_kind_and_its_wallet(TransactionKind kind, string expected)
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var transactionId = await SeedTransactionAsync(db, kind);

        await new EfCategorizationStore(db, Clock).ApplyAsync(transactionId,
            new CategorizationOutcome([Coffee(250m)], new DateOnly(2026, 9, 21), TransactionKind: kind), TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        var revision = await db.TransactionRevisions.SingleAsync(TestContext.Current.CancellationToken);
        using var snapshot = JsonDocument.Parse(revision.Snapshot);
        snapshot.RootElement.GetProperty("kind").GetString().Should().Be(expected);
        snapshot.RootElement.GetProperty("wallet_id").GetGuid().Should().Be(DefaultWalletId);
        snapshot.RootElement.GetProperty("raw_text").GetString().Should().Be("кофе 250", "the existing keys do not move");
    }

    [Fact]
    public async Task A_revision_records_the_kind_the_wallet_and_the_stated_balance()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var transactionId = await SeedTransactionAsync(db);
        var store = new EfCategorizationStore(db, Clock);

        await store.ApplyAsync(transactionId,
            new CategorizationOutcome([Coffee(250m)], new DateOnly(2026, 9, 21), WalletId: DefaultWalletId),
            TestContext.Current.CancellationToken);
        await store.ApplyAsync(transactionId,
            new CategorizationOutcome([], new DateOnly(2026, 9, 21), JobKind.Correct, "на самом деле на главном 45 тысяч",
                TransactionKind.BalanceCheck, DefaultWalletId, new Money(45000m, CurrencyCode.Rsd)),
            TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        var revisions = await db.TransactionRevisions.OrderBy(r => r.RevisionNumber).ToListAsync(TestContext.Current.CancellationToken);

        using var expense = JsonDocument.Parse(revisions[0].Snapshot);
        expense.RootElement.GetProperty("kind").GetString().Should().Be("Expense");
        expense.RootElement.GetProperty("wallet_id").GetGuid().Should().Be(DefaultWalletId);
        expense.RootElement.GetProperty("stated_balance").ValueKind.Should().Be(JsonValueKind.Null);

        using var statement = JsonDocument.Parse(revisions[1].Snapshot);
        statement.RootElement.GetProperty("kind").GetString().Should().Be("BalanceCheck");
        statement.RootElement.GetProperty("wallet_id").GetGuid().Should().Be(DefaultWalletId);
        statement.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
        statement.RootElement.GetProperty("raw_text").GetString().Should().Be("кофе 250", "every existing key stays");
        var stated = statement.RootElement.GetProperty("stated_balance");
        stated.GetProperty("amount").ValueKind.Should().Be(JsonValueKind.String, "an amount is never a JSON number");
        decimal.Parse(stated.GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture).Should().Be(45000m);
        stated.GetProperty("currency").GetString().Should().Be("RSD");
    }

    static decimal AmountOf(JsonElement element, string name)
    {
        element.GetProperty(name).ValueKind.Should().Be(JsonValueKind.String, "an amount or a rate is never a JSON number");
        return decimal.Parse(element.GetProperty(name).GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    }

    static async Task<(Guid CashEur, Guid Venue)> SeedExchangeWalletAndVenueAsync(LedgerDbContext db)
    {
        var cashEur = new Wallet { Id = Guid.NewGuid(), Name = "Cash EUR", Currency = CurrencyCode.Eur, CreatedAt = Clock.GetUtcNow() };
        var venue = new Merchant { Id = Guid.NewGuid(), DisplayName = "Menjačnica Centar", Kind = MerchantKind.ExchangeVenue };
        db.AddRange(cashEur, venue);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (cashEur.Id, venue.Id);
    }

    static TransferFacts Exchange(Guid cashEur, Guid venue) => new(
        cashEur, new Money(100m, CurrencyCode.Eur), DefaultWalletId, new Money(11500m, CurrencyCode.Rsd),
        new Money(200m, CurrencyCode.Rsd), TransferLeg.To, new ExchangeRate(CurrencyCode.Eur, 117m, CurrencyCode.Rsd), venue);

    [Fact]
    public async Task A_transfer_snapshot_records_both_legs_the_fee_leg_the_stated_rate_and_the_venue()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var (cashEur, venue) = await SeedExchangeWalletAndVenueAsync(db);
        var transactionId = await SeedTransactionAsync(db);

        await new EfCategorizationStore(db, Clock).ApplyAsync(transactionId,
            new CategorizationOutcome([], new DateOnly(2026, 9, 21), TransactionKind: TransactionKind.Transfer,
                Transfer: Exchange(cashEur, venue)),
            TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        var revision = await db.TransactionRevisions.SingleAsync(TestContext.Current.CancellationToken);
        using var snapshot = JsonDocument.Parse(revision.Snapshot);
        var root = snapshot.RootElement;
        root.GetProperty("kind").GetString().Should().Be("Transfer");
        root.GetProperty("wallet_id").GetGuid().Should().Be(cashEur, "a transfer's record wallet is its source leg");
        var transfer = root.GetProperty("transfer");
        transfer.GetProperty("from_wallet_id").GetGuid().Should().Be(cashEur);
        AmountOf(transfer, "from_amount").Should().Be(100m);
        transfer.GetProperty("from_currency").GetString().Should().Be("EUR");
        transfer.GetProperty("to_wallet_id").GetGuid().Should().Be(DefaultWalletId);
        AmountOf(transfer, "to_amount").Should().Be(11500m);
        transfer.GetProperty("to_currency").GetString().Should().Be("RSD");
        transfer.GetProperty("fee_leg").GetInt32().Should().Be((int)TransferLeg.To);
        AmountOf(transfer, "stated_rate").Should().Be(117m);
        transfer.GetProperty("stated_rate_base").GetString().Should().Be("EUR");
        transfer.GetProperty("venue_merchant_id").GetGuid().Should().Be(venue);
        var fee = root.GetProperty("items").EnumerateArray().Single();
        fee.GetProperty("role").GetInt32().Should().Be((int)EntryRole.Fee);
        fee.GetProperty("category_slug").GetString().Should().Be("fees-charges");
        fee.GetProperty("categorized_by").GetInt32().Should().Be((int)CategorizationAuthority.Rule);
        AmountOf(fee, "amount").Should().Be(200m);
        fee.GetProperty("currency").GetString().Should().Be("RSD");
    }

    [Fact]
    public async Task An_expense_snapshot_gives_each_item_its_role_and_a_null_transfer()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var transactionId = await SeedTransactionAsync(db);

        await new EfCategorizationStore(db, Clock).ApplyAsync(transactionId,
            new CategorizationOutcome([Coffee(250m)], new DateOnly(2026, 9, 21)), TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        var revision = await db.TransactionRevisions.SingleAsync(TestContext.Current.CancellationToken);
        using var snapshot = JsonDocument.Parse(revision.Snapshot);
        snapshot.RootElement.GetProperty("transfer").ValueKind.Should().Be(JsonValueKind.Null);
        foreach (var key in new[] { "raw_text", "occurred_on", "items", "kind", "wallet_id", "stated_balance" })
            snapshot.RootElement.TryGetProperty(key, out _).Should().BeTrue($"the existing key {key} stays");
        var item = snapshot.RootElement.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("role").GetInt32().Should().Be((int)EntryRole.Principal);
        foreach (var key in new[] { "description", "amount", "currency", "category_slug", "merchant_id", "categorized_by" })
            item.TryGetProperty(key, out _).Should().BeTrue($"the existing item key {key} stays");
    }

    [Fact]
    public async Task A_recorded_exchange_is_an_initial_revision()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var (cashEur, venue) = await SeedExchangeWalletAndVenueAsync(db);
        var transactionId = await SeedTransactionAsync(db);

        await new EfCategorizationStore(db, Clock).ApplyAsync(transactionId,
            new CategorizationOutcome([], new DateOnly(2026, 9, 21), JobKind.RecordExchange,
                TransactionKind: TransactionKind.Transfer, Transfer: Exchange(cashEur, venue)),
            TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        (await db.TransactionRevisions.SingleAsync(TestContext.Current.CancellationToken)).Kind.Should().Be(
            RevisionKind.Initial, "RecordExchange is a slip's first recording; a reply correcting it arrives as Correct");
    }
}
