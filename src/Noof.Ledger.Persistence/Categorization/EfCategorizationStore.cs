using Microsoft.EntityFrameworkCore;
using Npgsql;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Balances;
using Noof.Ledger.Persistence.Receipts;
using Noof.Ledger.Persistence.Revisions;

namespace Noof.Ledger.Persistence.Categorization;

internal sealed class EfCategorizationStore(LedgerDbContext db, TimeProvider timeProvider) : ICategorizationStore
{
    const string FeesCategorySlug = "fees-charges";
    const string TransferFeeDescription = "Fee";

    // "The end of the ledger" for BalanceSql.AsOfAsync: later than any record's (occurred_on, occurred_at). A fixed far
    // date rather than DateOnly/DateTimeOffset.MaxValue, whose last tick Npgsql would have to truncate to microseconds.
    static readonly DateOnly EndOfLedgerDay = new(9999, 12, 31);
    static readonly DateTimeOffset EndOfLedgerInstant = new(9999, 12, 31, 0, 0, 0, TimeSpan.Zero);

    public async Task<CategorizationSubject?> GetSubjectAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var header = await (
            from t in db.Transactions.AsNoTracking()
            where t.Id == transactionId
            join w in db.Wallets.AsNoTracking() on t.WalletId equals (Guid?)w.Id into walletJoin
            from w in walletJoin.DefaultIfEmpty()
            select new
            {
                t.Id,
                t.RawText,
                t.TelegramChatId,
                t.BotMessageId,
                WalletName = w == null ? string.Empty : w.Name,
                WalletCurrency = w == null ? (CurrencyCode?)null : w.Currency,
                t.Status,
                t.OccurredAt,
                t.TimeZoneId,
                t.OccurredOn,
                t.CaptureKind,
                t.Kind,
                t.WalletId,
                t.FailureReason,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (header is null)
            return null;

        var lines = await (
            from li in db.LineItems.AsNoTracking()
            where li.TransactionId == transactionId
            join c in db.Categories.AsNoTracking() on li.CategoryId equals c.Id into categoryJoin
            from c in categoryJoin.DefaultIfEmpty()
            join m in db.Merchants.AsNoTracking() on li.MerchantId equals m.Id into merchantJoin
            from m in merchantJoin.DefaultIfEmpty()
            orderby li.Ordinal
            select new RecordedLine(
                li.Description,
                li.Amount,
                c == null ? null : c.Slug,
                c == null ? null : c.NameEn,
                m == null ? null : m.DisplayName,
                li.Role))
            .ToListAsync(cancellationToken);

        var balances = header.WalletId is { } walletId
            ? await new EfBalanceReadModel(db).BalanceOfAsync(walletId, cancellationToken)
            : [];

        var statement = header.Kind == TransactionKind.BalanceCheck
            ? await db.BalanceChecks.AsNoTracking()
                .Where(bc => bc.TransactionId == transactionId)
                .Select(bc => new BalanceStatement(bc.Stated, bc.ComputedBefore))
                .SingleOrDefaultAsync(cancellationToken)
            : null;

        var transfer = header.Kind == TransactionKind.Transfer
            ? await TransferViewAsync(transactionId, lines, cancellationToken)
            : null;

        var charges = await ChargesOfAsync(transactionId, header.WalletCurrency, lines, cancellationToken);

        var slip = await SlipFactsAsync(transactionId, transfer?.VenueName, cancellationToken);

        // A voice capture has no text until its transcript arrives, and none at all when nothing was heard;
        // the pipeline and the echo read that as empty, which is what it is. Only a Manual record has no chat,
        // and nothing categorises or echoes one, so 0 stands in for it.
        return new CategorizationSubject(
            header.Id, header.RawText ?? string.Empty, header.TelegramChatId ?? 0, header.BotMessageId, header.WalletName,
            header.Status, ZonedClock.LocalDate(header.OccurredAt, header.TimeZoneId), header.OccurredOn, lines,
            header.CaptureKind, header.Kind, header.WalletCurrency, balances, statement, WalletId: header.WalletId,
            Transfer: transfer, FailureReason: header.FailureReason, Charges: charges, Slip: slip);
    }

    async Task<TransferView?> TransferViewAsync(
        Guid transactionId, IReadOnlyList<RecordedLine> lines, CancellationToken cancellationToken)
    {
        var row = await (
            from tr in db.Transfers.AsNoTracking()
            where tr.TransactionId == transactionId
            join source in db.Wallets.AsNoTracking() on tr.FromWalletId equals source.Id
            join destination in db.Wallets.AsNoTracking() on tr.ToWalletId equals destination.Id
            join venue in db.Merchants.AsNoTracking() on tr.VenueMerchantId equals (Guid?)venue.Id into venueJoin
            from venue in venueJoin.DefaultIfEmpty()
            select new
            {
                Stored = tr,
                SourceName = source.Name,
                DestinationName = destination.Name,
                VenueName = venue == null ? null : venue.DisplayName,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
            return null;

        var balances = new EfBalanceReadModel(db);
        return new TransferView(
            row.Stored.FromWalletId, row.SourceName, row.Stored.From,
            row.Stored.ToWalletId, row.DestinationName, row.Stored.To,
            lines.SingleOrDefault(line => line.Role == EntryRole.Fee)?.Amount, row.Stored.FeeLeg, StatedRateOf(row.Stored),
            row.VenueName,
            await balances.BalanceOfAsync(row.Stored.FromWalletId, cancellationToken),
            await balances.BalanceOfAsync(row.Stored.ToWalletId, cancellationToken),
            await BalanceSql.AsOfAsync(
                db, row.Stored.FromWalletId, row.Stored.From.Currency, EndOfLedgerDay, EndOfLedgerInstant, transactionId,
                cancellationToken));
    }

    // transfers keeps the rate's base and amount only; its quote is whichever leg's currency the base is not.
    static ExchangeRate? StatedRateOf(Transfer transfer) =>
        transfer is { StatedRate: { } quoteAmount, StatedRateBase: { } baseCurrency }
            ? new ExchangeRate(
                baseCurrency, quoteAmount, baseCurrency == transfer.From.Currency ? transfer.To.Currency : transfer.From.Currency)
            : null;

    async Task<IReadOnlyList<ChargeView>> ChargesOfAsync(
        Guid transactionId, CurrencyCode? walletCurrency, IReadOnlyList<RecordedLine> lines, CancellationToken cancellationToken)
    {
        if (walletCurrency is not { } chargedIn)
            return [];

        var charges = await db.Charges.AsNoTracking()
            .Where(charge => charge.TransactionId == transactionId)
            .ToListAsync(cancellationToken);

        return [.. charges
            .OrderBy(charge => charge.Currency.Value, StringComparer.Ordinal)
            .Select(charge => new ChargeView(
                charge.Currency,
                lines
                    .Where(line => line.Role == EntryRole.Principal && line.Amount.Currency == charge.Currency)
                    .Sum(line => line.Amount.Amount),
                new Money(charge.ChargedAmount, chargedIn),
                new Money(charge.FeeAmount, chargedIn),
                charge.RateUsed,
                new FeeTerms(charge.FeePercent, charge.FeeFixed, charge.FeeMinimum),
                charge.Source))];
    }

    // A slip's evidence and the office it names (spec §3-§4): the echo's slip rows and a slip correction's request
    // text read them. The venue merchant (1b's TransferView.VenueName) is the office once a transfer names it; before
    // that, the name the slip printed.
    async Task<SlipFacts?> SlipFactsAsync(Guid transactionId, string? venueName, CancellationToken cancellationToken) =>
        await new EfReceiptStore(db, timeProvider).GetExchangeSlipAsync(transactionId, cancellationToken) is { } slip
            ? new SlipFacts(venueName ?? slip.SellerName, slip.SlipNumber, slip.Evidence)
            : null;

    public async Task ApplyAsync(Guid transactionId, CategorizationOutcome outcome, CancellationToken cancellationToken)
    {
        var transfer = TransferOf(transactionId, outcome);

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        // Row lock, first statement inside the transaction. PostgreSQL runs READ COMMITTED, so
        // without this a second concurrent ApplyAsync for the same transaction (a worker whose
        // lease expired mid-job plus a second host process, say) would see nothing to DELETE
        // (neither caller has committed yet), both INSERTs would succeed, and both would COMMIT -
        // doubling the bill. FOR UPDATE makes the second caller block here until the first commits
        // and releases the lock, so it then sees (and replaces) the first caller's rows instead of
        // adding to them.
        await db.Database.SqlQueryRaw<Guid>(
            "SELECT id FROM transactions WHERE id = @transactionId FOR UPDATE",
            new NpgsqlParameter("transactionId", transactionId))
            .ToListAsync(cancellationToken);

        // The precedence predicate lives in the DELETE statement itself, not in an `if` around it -
        // a Rule- or User-authored line (2 or 4) is never eligible for deletion by a model re-run,
        // and there is nothing downstream that could forget to check that, because there is nothing
        // to forget.
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM line_items WHERE transaction_id = @transactionId AND categorized_by <= @modelAuthority",
            [
                new NpgsqlParameter("transactionId", transactionId),
                new NpgsqlParameter("modelAuthority", (int)CategorizationAuthority.Model),
            ],
            cancellationToken);

        // A fee line is Rule-authored, so the delete above never reaches it: C# rewrites it on every apply, and deleting
        // it by role means no precedence rule can keep a stale fee alive (Phase 7 spec §1).
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM line_items WHERE transaction_id = @transactionId AND role = @feeRole",
            [
                new NpgsqlParameter("transactionId", transactionId),
                new NpgsqlParameter("feeRole", (int)EntryRole.Fee),
            ],
            cancellationToken);

        // A transfer has no principal lines by definition, so one the record kept from being an expense goes whoever
        // wrote it - nothing edits lines by hand yet (spec §2).
        if (transfer is not null)
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM line_items WHERE transaction_id = @transactionId AND role = @principalRole",
                [
                    new NpgsqlParameter("transactionId", transactionId),
                    new NpgsqlParameter("principalRole", (object)(int)EntryRole.Principal),
                ],
                cancellationToken);

        // Starts after whatever survived the delete above (a Rule- or User-authored line), so a
        // model re-run's fresh ordinals never collide with an ordinal a kept line already owns.
        var ordinal = 1 + (await db.LineItems.AsNoTracking()
            .Where(li => li.TransactionId == transactionId)
            .Select(li => (int?)li.Ordinal)
            .MaxAsync(cancellationToken) ?? 0);
        foreach (var item in outcome.Items)
        {
            // A receipt line names its own Ordinal - the receipt's own order (R-2) - and never the
            // auto-numbering below, which exists only for a text/voice capture's model-authored lines.
            var itemOrdinal = item.Ordinal ?? ordinal;
            db.LineItems.Add(new LineItem
            {
                Id = Guid.NewGuid(),
                TransactionId = transactionId,
                Description = item.Description,
                Amount = item.Amount,
                CategoryId = item.CategoryId,
                CategorizedBy = CategorizationAuthority.Model,
                MerchantId = item.MerchantId,
                Ordinal = itemOrdinal,
                ReceiptLineId = item.ReceiptLineId,
            });

            if (item.Ordinal is null)
                ordinal++;
        }

        if (transfer?.Fee is { } fee)
            await AddFeeLineAsync(transactionId, fee, TransferFeeDescription, ordinal, cancellationToken);

        var transaction = await db.Transactions.SingleAsync(t => t.Id == transactionId, cancellationToken);
        var statusBefore = transaction.Status;
        var walletBefore = transaction.WalletId;
        // A correction arriving for a cancelled record corrects it and leaves it cancelled; only Restore
        // brings it back.
        transaction.Status = statusBefore == TransactionStatus.Cancelled ? TransactionStatus.Cancelled : TransactionStatus.Completed;
        transaction.OccurredOn = outcome.OccurredOn;
        transaction.Kind = outcome.TransactionKind;
        transaction.WalletId = transfer?.FromWalletId ?? outcome.WalletId ?? transaction.WalletId;
        transaction.FailureReason = RecordFailureReason.None;

        await db.SaveChangesAsync(cancellationToken);
        var chargeFees = await ForeignCharges.RewriteAsync(db, transaction, outcome.Kind, walletBefore, outcome.Charged, cancellationToken);
        await AddChargeFeeLinesAsync(transactionId, chargeFees, cancellationToken);
        await LedgerPostings.RewriteAsync(db, transaction, outcome.StatedBalance, transfer, cancellationToken);
        await RevisionLog.AppendAsync(db, transaction, RevisionKindFor(outcome), outcome.Instruction,
            statusBefore, timeProvider.GetUtcNow(), cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    // Unreachable while ProposalMapper and RecordExchange give every transfer its legs, no principal lines, and a fee
    // only together with its leg. Checked before the database transaction opens, so a refusal writes nothing, and it
    // is what holds "fee_leg is null exactly when there is no fee line" on the way in.
    static TransferFacts? TransferOf(Guid transactionId, CategorizationOutcome outcome) => outcome switch
    {
        { TransactionKind: not TransactionKind.Transfer } => null,
        { Transfer: { } transfer, Items.Count: 0 } when transfer.Fee.HasValue == transfer.FeeLeg.HasValue => transfer,
        _ => throw new InvalidOperationException(
            $"Transfer {transactionId} needs its legs, no line items, and a fee only together with the leg it was taken on."),
    };

    // A fee line is C#'s, never the model's (Phase 7 spec §1): Rule-authored, in Fees & Charges, in its leg's currency.
    async Task AddFeeLineAsync(Guid transactionId, Money fee, string description, int ordinal, CancellationToken cancellationToken)
    {
        var feesCategoryId = await db.Categories.AsNoTracking()
            .Where(category => category.Slug == FeesCategorySlug)
            .Select(category => category.Id)
            .SingleAsync(cancellationToken);

        db.LineItems.Add(new LineItem
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
            Description = description,
            Amount = fee,
            CategoryId = feesCategoryId,
            CategorizedBy = CategorizationAuthority.Rule,
            MerchantId = null,
            Ordinal = ordinal,
            Role = EntryRole.Fee,
        });
    }

    // After the highest ordinal the record has, not ApplyAsync's running one: the running ordinal never moves past a
    // line that names its own ordinal.
    async Task AddChargeFeeLinesAsync(
        Guid transactionId, IReadOnlyList<(CurrencyCode Purchase, Money Fee)> fees, CancellationToken cancellationToken)
    {
        if (fees.Count == 0)
            return;

        var ordinal = 1 + (await db.LineItems.AsNoTracking()
            .Where(li => li.TransactionId == transactionId)
            .Select(li => (int?)li.Ordinal)
            .MaxAsync(cancellationToken) ?? 0);
        foreach (var (purchase, fee) in fees)
            await AddFeeLineAsync(transactionId, fee, $"Fee · {purchase.Value} purchase", ordinal++, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
    }

    // CategorizeReceipt runs twice for the same receipt (I-2, Phase 6 final review): once from
    // ExtractReceiptWorker's own hand-off (no Instruction - the first, Initial reading) and again from
    // a receipt correction routed here instead of record_transaction (always carries the operator's
    // Instruction) - the two are told apart the same way Correct already is, by whether one is present.
    static RevisionKind RevisionKindFor(CategorizationOutcome outcome) => outcome.Kind switch
    {
        JobKind.Categorize => RevisionKind.Initial,
        JobKind.CategorizeReceipt => outcome.Instruction is null ? RevisionKind.Initial : RevisionKind.Correction,
        JobKind.Correct => RevisionKind.Correction,
        JobKind.Reinterpret => RevisionKind.Edit,
        // A slip's first recording, by C# from the slip's evidence; a reply correcting it arrives as Correct.
        JobKind.RecordExchange => RevisionKind.Initial,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome.Kind, "No revision kind for this job kind."),
    };

    public Task MarkFailedAsync(Guid transactionId, RecordFailureReason reason, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(
            "UPDATE transactions SET status = @status, failure_reason = @reason WHERE id = @transactionId AND status = @captured",
            [
                new NpgsqlParameter("status", (int)TransactionStatus.Failed),
                new NpgsqlParameter("reason", (int)reason),
                new NpgsqlParameter("transactionId", transactionId),
                new NpgsqlParameter("captured", (object)(int)TransactionStatus.Captured),
            ],
            cancellationToken);
}
