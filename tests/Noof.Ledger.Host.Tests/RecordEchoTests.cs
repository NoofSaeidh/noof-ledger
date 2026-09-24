using System.Globalization;
using AwesomeAssertions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Domain;

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

        // Contains, not EndWith: this test's own EUR line differs from the wallet's RSD currency,
        // which correctly appends the M10 conversion warning after the Total line - a deviation
        // from the brief's literal EndWith, recorded in the task report.
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
    public void A_line_in_a_currency_other_than_the_wallets_warns_that_it_is_not_converted()
    {
        var line = new RecordedLine("такси", new Money(20m, CurrencyCode.Eur), "transport", "Transport", null);

        Echo.Compose(Record(lines: [Coffee, line])).Text
            .Should().EndWith("Not in the wallet's currency — no conversion yet.");
    }

    [Fact]
    public void A_line_in_the_wallets_own_currency_gets_no_conversion_warning()
    {
        Echo.Compose(Record()).Text.Should().NotContain("no conversion yet");
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

    [Fact]
    public void A_failed_correction_says_so_above_the_unchanged_record()
    {
        var echo = Echo.ComposeCorrectionFailure(Record());

        echo.Text.Should().Be(
            "Could not apply that correction — the record is unchanged.\n\n"
            + "Recorded — Cash · balance 0.00 RSD\n• кофе — 250.00 RSD · Food & Drink\n\nTotal: 250.00 RSD");
        echo.Actions.Should().Equal(RecordAction.Cancel, RecordAction.Edit);
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
        Echo.TranscriptionFailure.Text.Should().Be("Couldn't transcribe that voice note.");
        Echo.TranscriptionFailure.Actions.Should().Equal(RecordAction.Edit);
    }

    [Fact]
    public void Hearing_nothing_in_a_spoken_correction_shows_the_record_unchanged_below()
    {
        var echo = Echo.ComposeHeardNothing(Record());

        echo.Text.Should().Be(
            "Heard nothing in that voice note.\n\nRecorded — Cash · balance 0.00 RSD\n• кофе — 250.00 RSD · Food & Drink\n\nTotal: 250.00 RSD");
        echo.Actions.Should().Equal(RecordAction.Cancel, RecordAction.Edit);
    }
}
