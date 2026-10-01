using System.Globalization;
using AwesomeAssertions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Domain;
using Npgsql;

namespace Noof.Ledger.Host.Tests;

public class RecordEchoTests
{
    static readonly IRecordEcho Echo = new RecordEcho();
    static readonly DateOnly Sent = new(2026, 9, 22);

    static RecordedLine Coffee => new("кофе", new Money(250m, CurrencyCode.Rsd), "food-drink", "Food & Drink", null);

    static CategorizationSubject Record(
        TransactionStatus status = TransactionStatus.Completed,
        DateOnly? occurredOn = null,
        IReadOnlyList<RecordedLine>? lines = null,
        TransactionKind kind = TransactionKind.Expense,
        CurrencyCode? walletCurrency = null,
        IReadOnlyList<Money>? walletBalances = null,
        BalanceStatement? statement = null) =>
        new(Guid.NewGuid(), "raw", 111L, 42, "Cash", status, Sent, occurredOn ?? Sent, lines ?? [Coffee],
            CaptureKind.Text, kind, walletCurrency ?? CurrencyCode.Rsd, walletBalances, statement);

    static readonly Guid SourceWalletId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    static readonly Guid DestinationWalletId = Guid.Parse("00000000-0000-0000-0000-0000000000a2");

    static Money Rsd(decimal amount) => new(amount, CurrencyCode.Rsd);
    static Money Eur(decimal amount) => new(amount, CurrencyCode.Eur);
    static Money Kzt(decimal amount) => new(amount, CurrencyCode.Kzt);

    static TransferView Legs(
        string fromWallet, Money from, Money fromBalance, string toWallet, Money to, Money toBalance,
        Money? fee = null, TransferLeg? feeLeg = null, ExchangeRate? statedRate = null, decimal? fromBalanceWithoutThis = null) =>
        new(SourceWalletId, fromWallet, from, DestinationWalletId, toWallet, to, fee, feeLeg, statedRate, VenueName: null,
            [fromBalance], [toBalance], fromBalanceWithoutThis);

    static CategorizationSubject TransferRecord(
        TransferView transfer, TransactionStatus status = TransactionStatus.Completed,
        IReadOnlyList<RecordedLine>? lines = null, DateOnly? occurredOn = null) =>
        new(Guid.NewGuid(), "raw", 111L, 42, transfer.FromWalletName, status, Sent, occurredOn ?? Sent, lines ?? [],
            CaptureKind.Text, TransactionKind.Transfer, transfer.From.Currency, transfer.FromBalances, Statement: null,
            WalletId: SourceWalletId, Transfer: transfer);

    static RecordedLine FeeLine(Money amount, string description = "Fee") =>
        new(description, amount, "fees-charges", "Fees & Charges", null, EntryRole.Fee);

    static TransferView Withdrawal =>
        Legs("Raiffeisen RSD", Rsd(10000m), Rsd(164150m), "Cash RSD", Rsd(10000m), Rsd(12000m));

    static TransferView ExchangeOf100Eur =>
        Legs("Cash EUR", Eur(100m), Eur(400m), "Cash RSD", Rsd(11700m), Rsd(23700m));

    static RecordedLine Taxi => new("taxi", new Money(30m, CurrencyCode.Usd), "transport", "Transport", null);

    static RecordedLine UsdPurchaseFee(decimal amount) => FeeLine(Kzt(amount), "Fee · USD purchase");

    static ChargeView UsdCharge(
        decimal charged = 15600m, decimal fee = 156m, decimal rate = 520m, ChargeSource source = ChargeSource.WalletTerms) =>
        new(CurrencyCode.Usd, 30m, Kzt(charged), Kzt(fee), rate, new FeeTerms(1m, null, null), source);

    static CategorizationSubject KaspiSpending(
        IReadOnlyList<RecordedLine> lines, IReadOnlyList<ChargeView> charges,
        TransactionStatus status = TransactionStatus.Completed) =>
        Record(status, lines: lines, walletCurrency: CurrencyCode.Kzt, walletBalances: [Kzt(184244m)])
            with { WalletName = "Kaspi KZT", Charges = charges };

    [Fact]
    public void A_recorded_line_is_echoed_with_its_balance_total_and_the_cancel_and_edit_buttons()
    {
        var echo = Echo.Compose(Record());

        echo.Text.Should().Be("Recorded — Cash · balance 0.00 RSD\n• кофе — 250.00 RSD · Food & Drink\n\nTotal: 250.00 RSD");
        echo.Actions.Should().Equal(RecordAction.Cancel, RecordAction.Edit);
    }

    [Fact]
    public void The_balance_line_reads_every_currency_the_wallet_holds_wallet_currency_first()
    {
        var balances = new[] { new Money(45230.50m, CurrencyCode.Rsd), new Money(20m, CurrencyCode.Eur) };

        Echo.Compose(Record(walletBalances: balances)).Text
            .Should().StartWith("Recorded — Cash · balance 45230.50 RSD, 20.00 EUR\n");
    }

    [Fact]
    public void A_merchant_follows_the_category()
    {
        var line = new RecordedLine("продукты", new Money(1000m, CurrencyCode.Eur), "groceries", "Groceries", "Lidl");

        Echo.Compose(Record(lines: [line])).Text.Should().Contain("• продукты — 1000.00 EUR · Groceries · Lidl");
    }

    [Fact]
    public void Totals_are_per_currency_and_ordered_by_code()
    {
        var lines = new[]
        {
            Coffee,
            new RecordedLine("такси", new Money(1000m, CurrencyCode.Eur), "transport", "Transport", null),
            new RecordedLine("хлеб", new Money(100m, CurrencyCode.Rsd), "groceries", "Groceries", null),
        };

        // Contains, not EndWith: the EUR line differs from the wallet's RSD currency, so the no-terms hint
        // follows the Total line.
        Echo.Compose(Record(lines: lines)).Text.Should().Contain("Total: 1000.00 EUR, 350.00 RSD");
    }

    [Fact]
    public void A_record_dated_to_another_day_says_which_day()
    {
        Echo.Compose(Record(occurredOn: new DateOnly(2026, 9, 21))).Text
            .Should().StartWith("Recorded — Cash · balance 0.00 RSD\nDate: 21.09.2026\n• кофе");
    }

    [Fact]
    public void A_record_dated_to_the_send_day_names_no_date()
    {
        Echo.Compose(Record()).Text.Should().NotContain("Date:");
    }

    [Fact]
    public void A_cancelled_record_shows_its_balance_and_offers_only_restore()
    {
        var echo = Echo.Compose(Record(TransactionStatus.Cancelled, walletBalances: [new Money(45230m, CurrencyCode.Rsd)]));

        echo.Text.Should().Be("Cancelled — Cash · balance 45230.00 RSD\n• кофе — 250.00 RSD · Food & Drink\n\nTotal: 250.00 RSD");
        echo.Actions.Should().Equal(RecordAction.Restore);
    }

    [Fact]
    public void A_read_that_found_no_spending_says_so_and_offers_only_edit()
    {
        var echo = Echo.Compose(Record(lines: []));

        echo.Text.Should().Be("Cash: found no spending here — nothing recorded.");
        echo.Actions.Should().Equal(RecordAction.Edit);
    }

    [Fact]
    public void An_income_record_is_echoed_with_the_income_prefix_and_its_balance()
    {
        var line = new RecordedLine("зарплата", new Money(2000m, CurrencyCode.Eur), "salary", "Salary", null);
        var echo = Echo.Compose(Record(
            kind: TransactionKind.Income, lines: [line], walletCurrency: CurrencyCode.Eur,
            walletBalances: [new Money(3200m, CurrencyCode.Eur)]));

        echo.Text.Should().Be("Income — Cash · balance 3200.00 EUR\n• зарплата — 2000.00 EUR · Salary\n\nTotal: 2000.00 EUR");
        echo.Actions.Should().Equal(RecordAction.Cancel, RecordAction.Edit);
    }

    [Fact]
    public void An_income_record_with_no_lines_says_so_and_offers_only_edit()
    {
        var echo = Echo.Compose(Record(kind: TransactionKind.Income, lines: []));

        echo.Text.Should().Be("Cash: found no income here — nothing recorded.");
        echo.Actions.Should().Equal(RecordAction.Edit);
    }

    [Fact]
    public void A_balance_statement_that_matches_says_so()
    {
        var statement = new BalanceStatement(new Money(44800.00m, CurrencyCode.Rsd), 44800.00m);
        var echo = Echo.Compose(Record(kind: TransactionKind.BalanceCheck, lines: [], statement: statement));

        echo.Text.Should().Be("Cash: balance was 44800.00 RSD, you said 44800.00 RSD — matches");
        echo.Actions.Should().Equal(RecordAction.Cancel, RecordAction.Edit);
    }

    [Fact]
    public void A_balance_statement_above_the_computed_balance_says_adjusted_up()
    {
        var statement = new BalanceStatement(new Money(45000.00m, CurrencyCode.Rsd), 44800.00m);
        var echo = Echo.Compose(Record(kind: TransactionKind.BalanceCheck, lines: [], statement: statement));

        echo.Text.Should().Be("Cash: balance was 44800.00 RSD, you said 45000.00 RSD — adjusted +200.00 RSD");
    }

    [Fact]
    public void A_balance_statement_below_the_computed_balance_says_adjusted_down()
    {
        var statement = new BalanceStatement(new Money(44500.00m, CurrencyCode.Rsd), 44800.00m);
        var echo = Echo.Compose(Record(kind: TransactionKind.BalanceCheck, lines: [], statement: statement));

        echo.Text.Should().Be("Cash: balance was 44800.00 RSD, you said 44500.00 RSD — adjusted -300.00 RSD");
    }

    [Fact]
    public void A_cancelled_balance_statement_shows_the_stated_amount_as_its_body()
    {
        var statement = new BalanceStatement(new Money(45000.00m, CurrencyCode.Rsd), 44800.00m);
        var echo = Echo.Compose(Record(
            TransactionStatus.Cancelled, kind: TransactionKind.BalanceCheck, lines: [], statement: statement));

        echo.Text.Should().Be("Cancelled — Cash · balance 0.00 RSD\nStatement: 45000.00 RSD");
        echo.Actions.Should().Equal(RecordAction.Restore);
    }

    [Fact]
    public void A_foreign_currency_with_no_charge_asks_for_a_rate_on_the_wallets_page()
    {
        var line = new RecordedLine("такси", new Money(20m, CurrencyCode.Eur), "transport", "Transport", null);

        Echo.Compose(Record(lines: [Coffee, line])).Text
            .Should().EndWith("\n20.00 EUR not converted — set a EUR rate for Cash on /wallets, or correct this record to apply it");
    }

    [Fact]
    public void A_line_in_the_wallets_own_currency_gets_no_conversion_line()
    {
        Echo.Compose(Record()).Text.Should().NotContain("not converted").And.NotContain("charged");
    }

    [Fact]
    public void A_failed_record_is_the_failure_echo()
    {
        Echo.Compose(Record(TransactionStatus.Failed, lines: [])).Should().Be(Echo.Failure);
        Echo.Failure.Actions.Should().Equal(RecordAction.Edit);
    }

    [Fact]
    public void A_record_still_being_read_is_the_acknowledgement_without_buttons()
    {
        var echo = Echo.Compose(Record(TransactionStatus.Captured, lines: []));

        echo.Text.Should().Be(Echo.Acknowledgement);
        echo.Actions.Should().BeEmpty();
    }

    // R2-3 follow-up: a Captured photo has no transcript to acknowledge - it is a receipt still
    // being read, so it gets the same "Reading the receipt…" wording ComposeReceipt and the initial
    // capture acknowledgement (TelegramUpdateRouter) already give this state, not the generic
    // text/voice "Recording…".
    [Fact]
    public void A_captured_photo_still_being_read_says_so_not_Recording()
    {
        var echo = Echo.Compose(Record(TransactionStatus.Captured, lines: []) with { CaptureKind = CaptureKind.Photo });

        echo.Text.Should().Be(Echo.ReadingReceipt);
        echo.Actions.Should().BeEmpty();
    }

    [Fact]
    public void A_failed_correction_says_so_above_the_unchanged_record()
    {
        var echo = Echo.ComposeCorrectionFailure(Record());

        echo.Text.Should().Be(
            "Could not apply that correction — the record is unchanged.\n\n"
            + "Recorded — Cash · balance 0.00 RSD\n• кофе — 250.00 RSD · Food & Drink\n\nTotal: 250.00 RSD");
        echo.Actions.Should().Equal(RecordAction.Cancel, RecordAction.Edit);
    }

    // R2-3 (Phase 6 second re-review): a deferred correction that exhausts its attempts on a Captured
    // photo used to call ComposeCorrectionFailure, which falls through to Compose - rendering
    // "…the record is unchanged.\n\nRecording…" for a receipt that was never being "recorded" in the
    // text/voice sense at all.
    [Fact]
    public void A_failed_correction_on_a_captured_photo_says_so_above_reading_the_receipt_not_recording()
    {
        var echo = Echo.ComposeCorrectionFailure(Record(TransactionStatus.Captured, lines: []) with { CaptureKind = CaptureKind.Photo });

        echo.Text.Should().Be($"Could not apply that correction — the record is unchanged.\n\n{Echo.ReadingReceipt}");
    }

    [Fact]
    public void A_line_with_no_category_says_so()
    {
        var line = new RecordedLine("штраф", new Money(5m, CurrencyCode.Eur), null, null, null);

        Echo.Compose(Record(lines: [line])).Text.Should().Contain("• штраф — 5.00 EUR · uncategorised");
    }

    [Fact]
    public void Amounts_render_the_same_under_a_Russian_machine_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
        try
        {
            Echo.Compose(Record()).Text.Should().Contain("250.00 RSD");
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    static CategorizationSubject Voice(
        string heard, TransactionStatus status = TransactionStatus.Completed, IReadOnlyList<RecordedLine>? lines = null) =>
        new(Guid.NewGuid(), heard, 111L, 42, "Cash", status, Sent, Sent, lines ?? [Coffee], CaptureKind.Voice,
            TransactionKind.Expense, CurrencyCode.Rsd, WalletBalances: null, Statement: null);

    [Fact]
    public void Transcribing_is_the_voice_notes_acknowledgement()
    {
        Echo.Transcribing.Should().Be("🎤 Transcribing…");
    }

    [Fact]
    public void A_voice_record_starts_with_what_was_heard()
    {
        var echo = Echo.Compose(Voice("кофе двести пятьдесят"));

        echo.Text.Should().Be(
            "🎤 \"кофе двести пятьдесят\"\nRecorded — Cash · balance 0.00 RSD\n• кофе — 250.00 RSD · Food & Drink\n\nTotal: 250.00 RSD");
        echo.Actions.Should().Equal(RecordAction.Cancel, RecordAction.Edit);
    }

    [Fact]
    public void A_typed_record_has_no_heard_line()
    {
        Echo.Compose(Record()).Text.Should().StartWith("Recorded — Cash");
    }

    [Fact]
    public void A_voice_record_with_no_transcript_shows_no_heard_line()
    {
        Echo.Compose(Voice(heard: "")).Text.Should().StartWith("Recorded — Cash");
    }

    [Fact]
    public void A_voice_note_waiting_for_its_transcript_says_it_is_transcribing()
    {
        var echo = Echo.Compose(Voice(heard: "", status: TransactionStatus.Captured, lines: []));

        echo.Text.Should().Be("🎤 Transcribing…");
        echo.Actions.Should().BeEmpty();
    }

    [Fact]
    public void A_voice_note_being_read_shows_what_was_heard_above_the_acknowledgement()
    {
        Echo.Compose(Voice("кофе 250", status: TransactionStatus.Captured, lines: [])).Text
            .Should().Be("🎤 \"кофе 250\"\nRecording…");
    }

    [Fact]
    public void A_cancelled_voice_record_keeps_what_was_heard()
    {
        Echo.Compose(Voice("кофе 250", status: TransactionStatus.Cancelled)).Text
            .Should().StartWith("🎤 \"кофе 250\"\nCancelled — Cash");
    }

    [Fact]
    public void Heard_nothing_says_so_and_offers_edit()
    {
        Echo.HeardNothing.Text.Should().Be("Heard nothing in that voice note.");
        Echo.HeardNothing.Actions.Should().Equal(RecordAction.Edit);
    }

    [Fact]
    public void A_note_that_could_not_be_transcribed_says_so_and_offers_edit()
    {
        Echo.TranscriptionFailure.Text.Should().Contain("Couldn't transcribe that voice note");
        Echo.TranscriptionFailure.Text.Should().Contain("Reply to this message");
        Echo.TranscriptionFailure.Actions.Should().Equal(RecordAction.Edit);
    }

    [Fact]
    public void An_unreadable_receipt_says_so_and_suggests_the_QR_link_and_offers_no_actions()
    {
        Echo.ReceiptUnreadable.Text.Should().Be(
            "I couldn't read this receipt reliably, so nothing was recorded. " + "Send the link from the receipt's QR code (scan it with your phone camera).");
        Echo.ReceiptUnreadable.Actions.Should().BeEmpty();
    }

    [Fact]
    public void A_receipt_that_could_not_be_read_says_what_to_try_instead()
    {
        Echo.ReceiptReadFailure.Text.Should().Contain("Couldn't read that receipt");
        Echo.ReceiptReadFailure.Text.Should().Contain("the link from the receipt's QR code");
        Echo.ReceiptReadFailure.Text.Should().NotContain("as a file");
    }

    [Fact]
    public void Hearing_nothing_in_a_spoken_correction_shows_the_record_unchanged_below()
    {
        var echo = Echo.ComposeHeardNothing(Record());

        echo.Text.Should().Be(
            "Heard nothing in that voice note.\n\nRecorded — Cash · balance 0.00 RSD\n• кофе — 250.00 RSD · Food & Drink\n\nTotal: 250.00 RSD");
        echo.Actions.Should().Equal(RecordAction.Cancel, RecordAction.Edit);
    }

    [Fact]
    public void A_retry_notice_names_the_step_the_reason_and_the_next_attempt_time()
    {
        var echo = Echo.ComposeReceiptExtractionRetryNotice(new NpgsqlException("connection refused"),
            new DateTimeOffset(2026, 9, 27, 14, 32, 0, TimeSpan.FromHours(2)));

        echo.Text.Should().Contain("Reading the receipt");
        echo.Text.Should().Contain("a database error");
        echo.Text.Should().Contain("14:32");
        echo.Actions.Should().BeEmpty();
    }

    [Fact]
    public void A_transfer_names_its_amount_and_both_wallets_and_shows_both_balances()
    {
        var echo = Echo.Compose(TransferRecord(Withdrawal));

        echo.Text.Should().Be(
            "Transfer — 10000.00 RSD · Raiffeisen RSD → Cash RSD\n"
            + "Raiffeisen RSD · balance 164150.00 RSD\n"
            + "Cash RSD · balance 12000.00 RSD");
        echo.Actions.Should().Equal(RecordAction.Cancel, RecordAction.Edit);
    }

    [Fact]
    public void A_transfer_with_a_fee_on_the_source_shows_it_inside_the_source_leg_and_on_its_own_line()
    {
        var transfer = Legs("Raiffeisen RSD", Rsd(10150m), Rsd(164000m), "Cash RSD", Rsd(10000m), Rsd(12000m),
            fee: Rsd(150m), feeLeg: TransferLeg.From);

        var echo = Echo.Compose(TransferRecord(transfer, lines: [FeeLine(Rsd(150m))]));

        echo.Text.Should().Be(
            "Transfer — Raiffeisen RSD -10150.00 RSD (incl. fee 150.00) → Cash RSD +10000.00 RSD\n"
            + "Fee 150.00 RSD · Fees & Charges\n"
            + "Raiffeisen RSD · balance 164000.00 RSD\n"
            + "Cash RSD · balance 12000.00 RSD");
        echo.Actions.Should().Equal(RecordAction.Cancel, RecordAction.Edit);
    }

    [Fact]
    public void A_transfer_with_a_fee_on_the_destination_shows_what_arrived_after_the_fee()
    {
        var transfer = Legs("Raiffeisen RSD", Rsd(10000m), Rsd(164150m), "Cash RSD", Rsd(9850m), Rsd(11850m),
            fee: Rsd(150m), feeLeg: TransferLeg.To);

        Echo.Compose(TransferRecord(transfer, lines: [FeeLine(Rsd(150m))])).Text.Should().Be(
            "Transfer — Raiffeisen RSD -10000.00 RSD → Cash RSD +9850.00 RSD (after fee 150.00)\n"
            + "Fee 150.00 RSD · Fees & Charges\n"
            + "Raiffeisen RSD · balance 164150.00 RSD\n"
            + "Cash RSD · balance 11850.00 RSD");
    }

    [Fact]
    public void An_exchange_shows_both_sides_their_wallets_and_the_rate_derived_from_them()
    {
        var echo = Echo.Compose(TransferRecord(ExchangeOf100Eur));

        echo.Text.Should().Be(
            "Exchange — 100.00 EUR (Cash EUR) → 11700.00 RSD (Cash RSD) · 1 EUR = 117.0000 RSD\n"
            + "Cash EUR · balance 400.00 EUR\n"
            + "Cash RSD · balance 23700.00 RSD");
        echo.Actions.Should().Equal(RecordAction.Cancel, RecordAction.Edit);
    }

    // 33.33 × 117.35 = 3911.2755, stored as 3911.28: re-deriving from the rounded amounts would say 117.3501.
    [Fact]
    public void An_exchange_shows_the_rate_the_operator_stated_not_one_derived_from_rounded_amounts()
    {
        var transfer = Legs("Cash EUR", Eur(33.33m), Eur(400m), "Cash RSD", Rsd(3911.28m), Rsd(15911.28m),
            statedRate: new ExchangeRate(CurrencyCode.Eur, 117.35m, CurrencyCode.Rsd));

        Echo.Compose(TransferRecord(transfer)).Text.Should().StartWith(
            "Exchange — 33.33 EUR (Cash EUR) → 3911.28 RSD (Cash RSD) · 1 EUR = 117.3500 RSD\n");
    }

    [Fact]
    public void An_exchange_with_a_fee_on_the_source_shows_it_on_that_side_and_takes_the_rate_from_the_principals()
    {
        var transfer = Legs("Cash EUR", Eur(102m), Eur(398m), "Cash RSD", Rsd(11700m), Rsd(23700m),
            fee: Eur(2m), feeLeg: TransferLeg.From);

        Echo.Compose(TransferRecord(transfer, lines: [FeeLine(Eur(2m))])).Text.Should().Be(
            "Exchange — 102.00 EUR (Cash EUR, incl. fee 2.00) → 11700.00 RSD (Cash RSD) · 1 EUR = 117.0000 RSD\n"
            + "Fee 2.00 EUR · Fees & Charges\n"
            + "Cash EUR · balance 398.00 EUR\n"
            + "Cash RSD · balance 23700.00 RSD");
    }

    [Fact]
    public void An_exchange_with_a_fee_on_the_destination_shows_it_on_that_side_and_takes_the_rate_from_the_principals()
    {
        var transfer = Legs("Cash EUR", Eur(100m), Eur(400m), "Cash RSD", Rsd(11500m), Rsd(23500m),
            fee: Rsd(200m), feeLeg: TransferLeg.To);

        Echo.Compose(TransferRecord(transfer, lines: [FeeLine(Rsd(200m))])).Text.Should().Be(
            "Exchange — 100.00 EUR (Cash EUR) → 11500.00 RSD (Cash RSD, after fee 200.00) · 1 EUR = 117.0000 RSD\n"
            + "Fee 200.00 RSD · Fees & Charges\n"
            + "Cash EUR · balance 400.00 EUR\n"
            + "Cash RSD · balance 23500.00 RSD");
    }

    [Fact]
    public void A_transfer_dated_to_another_day_says_which_day()
    {
        Echo.Compose(TransferRecord(Withdrawal, occurredOn: new DateOnly(2026, 9, 21))).Text.Should().StartWith(
            "Transfer — 10000.00 RSD · Raiffeisen RSD → Cash RSD\nDate: 21.09.2026\nRaiffeisen RSD · balance");
    }

    [Fact]
    public void A_cancelled_transfer_re_renders_both_balances_and_offers_only_restore()
    {
        var transfer = Legs("Raiffeisen RSD", Rsd(10000m), Rsd(174150m), "Cash RSD", Rsd(10000m), Rsd(2000m));

        var echo = Echo.Compose(TransferRecord(transfer, TransactionStatus.Cancelled));

        echo.Text.Should().Be(
            "Cancelled transfer — 10000.00 RSD · Raiffeisen RSD → Cash RSD\n"
            + "Raiffeisen RSD · balance 174150.00 RSD\n"
            + "Cash RSD · balance 2000.00 RSD");
        echo.Actions.Should().Equal(RecordAction.Restore);
    }

    [Fact]
    public void A_cancelled_exchange_says_so_and_offers_only_restore()
    {
        var echo = Echo.Compose(TransferRecord(ExchangeOf100Eur, TransactionStatus.Cancelled));

        echo.Text.Should().StartWith("Cancelled exchange — 100.00 EUR (Cash EUR) → 11700.00 RSD (Cash RSD) · 1 EUR = 117.0000 RSD\n");
        echo.Actions.Should().Equal(RecordAction.Restore);
    }

    [Fact]
    public void A_wallet_with_no_balance_yet_reads_zero_in_its_own_currency()
    {
        Echo.Compose(TransferRecord(Withdrawal with { ToBalances = [] })).Text.Should().EndWith("\nCash RSD · balance 0.00 RSD");
    }

    [Fact]
    public void Transfer_amounts_and_the_rate_render_the_same_under_a_Serbian_machine_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sr-Latn-RS");
        try
        {
            Echo.Compose(TransferRecord(ExchangeOf100Eur)).Text.Should().StartWith(
                "Exchange — 100.00 EUR (Cash EUR) → 11700.00 RSD (Cash RSD) · 1 EUR = 117.0000 RSD\n");
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    const string Crossed = "is now";

    [Fact]
    public void An_exchange_that_takes_the_source_below_zero_asks_what_is_missing()
    {
        var transfer = Legs("Cash EUR", Eur(1100m), Eur(-1000m), "Cash RSD", Rsd(128700m), Rsd(140400m),
            fromBalanceWithoutThis: 100m);

        Echo.Compose(TransferRecord(transfer)).Text.Should().Be(
            "Exchange — 1100.00 EUR (Cash EUR) → 128700.00 RSD (Cash RSD) · 1 EUR = 117.0000 RSD\n"
            + "Cash EUR · balance -1000.00 EUR\n"
            + "Cash RSD · balance 140400.00 RSD\n"
            + "Cash EUR is now -1000.00 EUR — a missing exchange or income?");
    }

    [Fact]
    public void A_source_at_exactly_zero_without_the_transfer_has_crossed()
    {
        var transfer = Legs("Raiffeisen RSD", Rsd(5000m), Rsd(-5000m), "Cash RSD", Rsd(5000m), Rsd(5000m),
            fromBalanceWithoutThis: 0m);

        Echo.Compose(TransferRecord(transfer)).Text.Should().EndWith(
            "\nRaiffeisen RSD is now -5000.00 RSD — a missing exchange or income?");
    }

    [Fact]
    public void A_source_that_was_already_negative_stays_quiet()
    {
        var transfer = Legs("Visa RSD", Rsd(5000m), Rsd(-25000m), "Cash RSD", Rsd(5000m), Rsd(5000m),
            fromBalanceWithoutThis: -20000m);

        Echo.Compose(TransferRecord(transfer)).Text.Should().NotContain(Crossed);
    }

    // Backdated before a checkpoint on the source: the checkpoint absorbs it, so the transfer moved nothing on
    // that wallet, and its balance was below zero with or without it.
    [Fact]
    public void A_backdated_transfer_absorbed_by_a_later_checkpoint_stays_quiet()
    {
        var transfer = Legs("Cash EUR", Eur(100m), Eur(-50m), "Cash RSD", Rsd(11700m), Rsd(23700m),
            fromBalanceWithoutThis: -50m);

        Echo.Compose(TransferRecord(transfer)).Text.Should().NotContain(Crossed);
    }

    [Fact]
    public void A_source_left_at_exactly_zero_has_not_crossed()
    {
        var transfer = Legs("Raiffeisen RSD", Rsd(5000m), Rsd(0m), "Cash RSD", Rsd(5000m), Rsd(5000m),
            fromBalanceWithoutThis: 5000m);

        Echo.Compose(TransferRecord(transfer)).Text.Should().NotContain(Crossed);
    }

    [Fact]
    public void A_source_with_no_balance_without_this_transfer_stays_quiet()
    {
        var transfer = Legs("Cash EUR", Eur(1100m), Eur(-1000m), "Cash RSD", Rsd(128700m), Rsd(140400m));

        Echo.Compose(TransferRecord(transfer)).Text.Should().NotContain(Crossed);
    }

    [Fact]
    public void Only_the_balance_in_the_transfers_own_currency_counts()
    {
        // The RSD entry first on purpose: without the currency filter the first entry is what would be read.
        var transfer = ExchangeOf100Eur with { FromBalances = [Rsd(-50m), Eur(20m)], FromBalanceWithoutThis = 120m };

        Echo.Compose(TransferRecord(transfer)).Text.Should().NotContain(Crossed);
    }

    [Fact]
    public void A_source_with_no_balance_in_the_transfers_currency_stays_quiet()
    {
        var transfer = ExchangeOf100Eur with { FromBalances = [Rsd(-50m)], FromBalanceWithoutThis = 120m };

        Echo.Compose(TransferRecord(transfer)).Text.Should().NotContain(Crossed);
    }

    [Fact]
    public void Cancel_and_restore_turn_the_crossing_line_off_and_on_again()
    {
        var transfer = Legs("Cash EUR", Eur(100m), Eur(-50m), "Cash RSD", Rsd(11700m), Rsd(23700m),
            fromBalanceWithoutThis: 50m);

        Echo.Compose(TransferRecord(transfer, TransactionStatus.Cancelled)).Text.Should().NotContain(Crossed);
        Echo.Compose(TransferRecord(transfer, TransactionStatus.Completed)).Text
            .Should().EndWith("\nCash EUR is now -50.00 EUR — a missing exchange or income?");
    }

    [Fact]
    public void A_foreign_spending_charged_at_the_wallets_terms_shows_the_charge_its_rate_and_its_fee()
    {
        var echo = Echo.Compose(KaspiSpending([Taxi, UsdPurchaseFee(156m)], [UsdCharge()]));

        echo.Text.Should().Be(
            "Recorded — Kaspi KZT · balance 184244.00 KZT\n"
            + "• taxi — 30.00 USD · Transport\n"
            + "\n"
            + "Total: 30.00 USD\n"
            + "30.00 USD → charged 15600.00 KZT (1 USD = 520.0000 KZT, wallet rate) + fee 156.00 KZT");
        echo.Actions.Should().Equal(RecordAction.Cancel, RecordAction.Edit);
    }

    [Fact]
    public void A_stated_charge_says_stated_and_shows_the_rate_it_works_out_to()
    {
        var charge = UsdCharge(charged: 15400m, fee: 154m, rate: 513.333333333333m, source: ChargeSource.Stated);

        Echo.Compose(KaspiSpending([Taxi, UsdPurchaseFee(154m)], [charge])).Text.Should().EndWith(
            "\n30.00 USD → charged 15400.00 KZT (1 USD = 513.3333 KZT, stated) + fee 154.00 KZT");
    }

    [Fact]
    public void A_charge_that_cost_no_fee_names_no_fee()
    {
        Echo.Compose(KaspiSpending([Taxi], [UsdCharge(fee: 0m)])).Text.Should().EndWith(
            "\n30.00 USD → charged 15600.00 KZT (1 USD = 520.0000 KZT, wallet rate)");
    }

    [Fact]
    public void Each_foreign_currency_gets_its_own_line_in_code_order()
    {
        var museum = new RecordedLine("museum", new Money(20m, CurrencyCode.Eur), "entertainment", "Entertainment", null);

        Echo.Compose(KaspiSpending([Taxi, museum, UsdPurchaseFee(156m)], [UsdCharge()])).Text.Should().EndWith(
            "\nTotal: 20.00 EUR, 30.00 USD\n"
            + "20.00 EUR not converted — set a EUR rate for Kaspi KZT on /wallets, or correct this record to apply it\n"
            + "30.00 USD → charged 15600.00 KZT (1 USD = 520.0000 KZT, wallet rate) + fee 156.00 KZT");
    }

    [Fact]
    public void A_cancelled_foreign_spending_keeps_its_charge_line()
    {
        var echo = Echo.Compose(KaspiSpending([Taxi, UsdPurchaseFee(156m)], [UsdCharge()], TransactionStatus.Cancelled));

        echo.Text.Should().StartWith("Cancelled — Kaspi KZT · balance 184244.00 KZT\n• taxi — 30.00 USD · Transport\n");
        echo.Text.Should().EndWith("\n30.00 USD → charged 15600.00 KZT (1 USD = 520.0000 KZT, wallet rate) + fee 156.00 KZT");
        echo.Actions.Should().Equal(RecordAction.Restore);
    }

    [Fact]
    public void A_foreign_income_line_still_says_it_is_not_converted()
    {
        var salary = new RecordedLine("зарплата", new Money(2000m, CurrencyCode.Eur), "salary", "Salary", null);

        Echo.Compose(Record(kind: TransactionKind.Income, lines: [salary])).Text
            .Should().EndWith("\nNot in the wallet's currency — no conversion yet.");
    }
}
