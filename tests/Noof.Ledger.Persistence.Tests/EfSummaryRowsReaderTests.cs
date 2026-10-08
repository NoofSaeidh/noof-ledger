using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Reporting.Summary;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Reporting;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfSummaryRowsReaderTests(PostgresFixture fixture)
{
    static readonly DateOnly September = new(2026, 9, 1);
    static readonly DateOnly October = new(2026, 10, 1);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static SummaryLineRow Line(
        Guid transaction, Guid wallet, CurrencyCode walletCurrency, SummaryLineKind kind, EntryRole role, int ordinal,
        string category, string? merchant, string description, Money amount, decimal? charged = null) =>
        new(transaction, IntegritySeed.Day, wallet, walletCurrency, kind, role, ordinal, category, merchant, description,
            amount.Amount, amount.Currency, charged);

    // A line written straight to the tables, for the shapes the write path never produces or the reader must refuse.
    static async Task<Guid> RawRecordAsync(
        LedgerDbContext db, Guid? walletId, DateOnly occurredOn, TransactionStatus status, TransactionKind kind,
        EntryRole role = EntryRole.Principal)
    {
        var id = await IntegritySeed.CaptureAsync(db, occurredOn);
        await db.Database.ExecuteSqlAsync(
            $"UPDATE transactions SET wallet_id = {walletId}, status = {(int)status}, kind = {(int)kind} WHERE id = {id}", Ct);
        await IntegritySeed.AddRawLineAsync(db, id, role, 10.00m, "RSD");
        return id;
    }

    [Fact]
    public async Task Reads_each_counted_line_on_its_wallet_with_its_kind_charge_and_the_records_merchant()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var m = await SummarySeed.SeedSeptemberAsync(db);

        var rows = await new EfSummaryRowsReader(db).ReadAsync(September, October, Ct);

        var (rsd, eur) = (CurrencyCode.Rsd, CurrencyCode.Eur);
        rows.Lines.Should().BeEquivalentTo(
        [
            Line(m.ForeignPurchase, m.Raiffeisen, rsd, SummaryLineKind.Spent, EntryRole.Principal, 1, "Groceries", "Maxi", "Groceries", IntegritySeed.Eur(20.00m), 3520.00m),
            Line(m.ForeignPurchase, m.Raiffeisen, rsd, SummaryLineKind.Spent, EntryRole.Principal, 2, "Coffee", "Maxi", "Coffee", IntegritySeed.Eur(5.00m), 3520.00m),
            Line(m.ForeignPurchase, m.Raiffeisen, rsd, SummaryLineKind.Spent, EntryRole.Principal, 3, "Groceries", "Maxi", "Groceries", IntegritySeed.Eur(5.00m), 3520.00m),
            Line(m.ForeignPurchase, m.Raiffeisen, rsd, SummaryLineKind.Spent, EntryRole.Fee, 4, "Fees & Charges", "Maxi", "Fee · EUR purchase", IntegritySeed.Rsd(35.20m)),
            Line(m.Withdrawal, m.Raiffeisen, rsd, SummaryLineKind.Spent, EntryRole.Fee, 1, "Fees & Charges", null, "Fee", IntegritySeed.Rsd(150.00m)),
            Line(m.FeeOnTo, m.CashEur, eur, SummaryLineKind.Spent, EntryRole.Fee, 1, "Fees & Charges", null, "Fee", IntegritySeed.Eur(2.00m)),
            Line(m.Exchange, m.Wise, eur, SummaryLineKind.Spent, EntryRole.Fee, 1, "Fees & Charges", "Exchange Office", "Fee", IntegritySeed.Eur(1.00m)),
            Line(m.Purchase, m.CashRsd, rsd, SummaryLineKind.Spent, EntryRole.Principal, 1, "Groceries", null, "Groceries", IntegritySeed.Rsd(500.00m)),
            Line(m.Refund, m.CashRsd, rsd, SummaryLineKind.Refund, EntryRole.Principal, 1, "Groceries", null, "Returned groceries", IntegritySeed.Rsd(800.00m)),
            Line(m.Refund, m.CashRsd, rsd, SummaryLineKind.Refund, EntryRole.Principal, 2, "Coffee", null, "Returned coffee", IntegritySeed.Rsd(100.00m)),
            Line(m.Salary, m.Wise, eur, SummaryLineKind.Received, EntryRole.Principal, 1, "Salary", null, "Salary", IntegritySeed.Eur(2800.00m)),
        ]);
    }

    [Fact]
    public async Task Counts_every_record_that_is_not_cancelled_and_only_the_lines_that_count_inside_the_range()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var wallet = await IntegritySeed.AddWalletAsync(db, "Cash", CurrencyCode.Rsd);
        var day = IntegritySeed.Day;
        var captured = await RawRecordAsync(db, wallet, day, TransactionStatus.Captured, TransactionKind.Expense);
        var failed = await RawRecordAsync(db, wallet, day, TransactionStatus.Failed, TransactionKind.Expense);
        await RawRecordAsync(db, wallet, day, TransactionStatus.Cancelled, TransactionKind.Expense);
        await RawRecordAsync(db, wallet, day, TransactionStatus.Completed, TransactionKind.BalanceCheck);
        await RawRecordAsync(db, wallet, day, TransactionStatus.Completed, TransactionKind.Transfer);
        await RawRecordAsync(db, wallet, day, TransactionStatus.Completed, TransactionKind.Income, EntryRole.Fee);
        await RawRecordAsync(db, walletId: null, day, TransactionStatus.Captured, TransactionKind.Expense);
        await RawRecordAsync(db, wallet, new DateOnly(2026, 8, 31), TransactionStatus.Completed, TransactionKind.Expense);
        await RawRecordAsync(db, wallet, October, TransactionStatus.Completed, TransactionKind.Expense);

        var rows = await new EfSummaryRowsReader(db).ReadAsync(September, October, Ct);

        rows.Lines.Select(line => line.TransactionId).Should().BeEquivalentTo([captured, failed],
            "a record counts unless it is cancelled (A-2); a statement, a principal line on a transfer, a fee line on an "
            + "income, a line with no wallet to count in and the days either side of the range never do");
    }

    [Fact]
    public async Task Reads_each_transfer_as_stored_with_its_fee_and_venue()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var m = await SummarySeed.SeedSeptemberAsync(db);
        var plain = await SummarySeed.TransferAsync(db, new TransferFacts(
            m.CashEur, IntegritySeed.Eur(50.00m), m.Wise, IntegritySeed.Eur(50.00m), null, null, StatedRate: null));
        var cancelled = await SummarySeed.TransferAsync(db, new TransferFacts(
            m.CashEur, IntegritySeed.Eur(20.00m), m.Wise, IntegritySeed.Eur(20.00m), null, null, StatedRate: null));
        await IntegritySeed.CancelAsync(db, cancelled);

        var rows = await new EfSummaryRowsReader(db).ReadAsync(September, October, Ct);

        var day = IntegritySeed.Day;
        rows.Transfers.Should().BeEquivalentTo(
        [
            new SummaryTransferRow(m.Withdrawal, day, m.Raiffeisen, IntegritySeed.Rsd(10150.00m), m.CashRsd, IntegritySeed.Rsd(10000.00m), TransferLeg.From, 150.00m, null),
            new SummaryTransferRow(m.FeeOnTo, day, m.Wise, IntegritySeed.Eur(200.00m), m.CashEur, IntegritySeed.Eur(198.00m), TransferLeg.To, 2.00m, null),
            new SummaryTransferRow(m.Exchange, day, m.Wise, IntegritySeed.Eur(100.00m), m.Raiffeisen, IntegritySeed.Rsd(11700.00m), TransferLeg.From, 1.00m, "Exchange Office"),
            new SummaryTransferRow(plain, day, m.CashEur, IntegritySeed.Eur(50.00m), m.Wise, IntegritySeed.Eur(50.00m), null, null, null),
        ]);
    }

    [Fact]
    public async Task Lists_every_wallet_with_the_start_of_its_whole_history()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var active = await IntegritySeed.AddWalletAsync(db, "Active RSD", CurrencyCode.Rsd);
        var movedInto = await IntegritySeed.AddWalletAsync(db, "Moved into EUR", CurrencyCode.Eur);
        var dormant = await IntegritySeed.AddWalletAsync(db, "Dormant RSD", CurrencyCode.Rsd);
        var unused = await IntegritySeed.AddWalletAsync(db, "Unused EUR", CurrencyCode.Eur);
        await RecordOnAsync(db, active, new DateOnly(2026, 3, 14));
        await RecordOnAsync(db, active, IntegritySeed.Day);
        await RecordOnAsync(db, dormant, new DateOnly(2026, 1, 20));
        await SummarySeed.TransferAsync(db, new TransferFacts(
            active, IntegritySeed.Rsd(1170.00m), movedInto, IntegritySeed.Eur(10.00m), null, null, StatedRate: null));
        await db.Wallets.Where(wallet => wallet.Id == movedInto || wallet.Id == dormant)
            .ExecuteUpdateAsync(set => set.SetProperty(wallet => wallet.Archived, true), Ct);

        var rows = await new EfSummaryRowsReader(db).ReadAsync(new DateOnly(2026, 6, 1), October, Ct);

        rows.Wallets.Should().BeEquivalentTo(
        [
            new SummaryWallet(active, "Active RSD", CurrencyCode.Rsd, Archived: false, HistoryStart: new DateOnly(2026, 3, 1)),
            new SummaryWallet(movedInto, "Moved into EUR", CurrencyCode.Eur, Archived: true, HistoryStart: null),
            new SummaryWallet(dormant, "Dormant RSD", CurrencyCode.Rsd, Archived: true, HistoryStart: new DateOnly(2026, 1, 1)),
            new SummaryWallet(unused, "Unused EUR", CurrencyCode.Eur, Archived: false, HistoryStart: null),
            new SummaryWallet(IntegritySeed.MainWalletId, IntegritySeed.MainWalletName, CurrencyCode.Rsd, Archived: false, HistoryStart: null),
        ], "every wallet is listed: the dormant one's lines all predate the four months read, yet its January start "
            + "still starts the history of all wallets (A-5); a transfer leg is no counted line, so the wallet only moved "
            + "into has no start");
    }

    static Task<Guid> RecordOnAsync(LedgerDbContext db, Guid walletId, DateOnly day) =>
        IntegritySeed.RecordAsync(
            db, IntegritySeed.Expense(walletId, IntegritySeed.Line(100.00m, CurrencyCode.Rsd)) with { OccurredOn = day }, day);

    [Theory]
    [InlineData(JobKind.Categorize, JobStatus.Pending)]
    [InlineData(JobKind.Transcribe, JobStatus.Claimed)]
    [InlineData(JobKind.CategorizeReceipt, JobStatus.Pending)]
    public async Task Work_queued_or_running_on_a_record_dated_in_the_month_is_unsettled(JobKind kind, JobStatus status)
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var record = await IntegritySeed.CaptureAsync(db, October);
        await PipelineSeed.JobAsync(db, record, kind, status, new DateTimeOffset(2026, 10, 1, 6, 0, 0, TimeSpan.Zero));

        (await new EfSummaryRowsReader(db).HasUnsettledWorkAsync(October, Ct)).Should().BeTrue(
            "a message sent on the 1st saying 'yesterday' stays dated the 1st until it is read, so the work counts from the month's first day on");
    }

    [Fact]
    public async Task Finished_work_work_on_an_earlier_day_and_work_on_a_cancelled_record_are_settled()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var reader = new EfSummaryRowsReader(db);
        var queuedAt = new DateTimeOffset(2026, 10, 1, 6, 0, 0, TimeSpan.Zero);

        (await reader.HasUnsettledWorkAsync(October, Ct)).Should().BeFalse();

        var done = await IntegritySeed.CaptureAsync(db, October);
        await PipelineSeed.JobAsync(db, done, JobKind.Categorize, JobStatus.Succeeded, queuedAt);
        await PipelineSeed.JobAsync(db, done, JobKind.Correct, JobStatus.Failed, queuedAt);
        var earlier = await IntegritySeed.CaptureAsync(db, new DateOnly(2026, 9, 30));
        await PipelineSeed.JobAsync(db, earlier, JobKind.Categorize, JobStatus.Pending, queuedAt);
        var cancelled = await IntegritySeed.CaptureAsync(db, October);
        await PipelineSeed.JobAsync(db, cancelled, JobKind.Categorize, JobStatus.Pending, queuedAt);
        await db.Database.ExecuteSqlAsync(
            $"UPDATE transactions SET status = {(int)TransactionStatus.Cancelled} WHERE id = {cancelled}", Ct);

        (await reader.HasUnsettledWorkAsync(October, Ct)).Should().BeFalse();
    }
}
