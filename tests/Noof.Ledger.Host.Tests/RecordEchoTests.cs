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
        TransactionStatus status = TransactionStatus.Completed, DateOnly? occurredOn = null,
        IReadOnlyList<RecordedLine>? lines = null) =>
        new(Guid.NewGuid(), "raw", 111L, 42, "Cash", status, Sent, occurredOn ?? Sent, lines ?? [Coffee]);

    [Fact]
    public void A_recorded_line_is_echoed_with_its_total_and_the_cancel_and_edit_buttons()
    {
        var echo = Echo.Compose(Record());

        echo.Text.Should().Be("Recorded — Cash\n• кофе — 250.00 RSD · Food & Drink\n\nTotal: 250.00 RSD");
        echo.Actions.Should().Equal(RecordAction.Cancel, RecordAction.Edit);
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

        Echo.Compose(Record(lines: lines)).Text.Should().EndWith("Total: 1000.00 EUR, 350.00 RSD");
    }

    [Fact]
    public void A_record_dated_to_another_day_says_which_day()
    {
        Echo.Compose(Record(occurredOn: new DateOnly(2026, 9, 21))).Text
            .Should().StartWith("Recorded — Cash\nDate: 21.09.2026\n• кофе");
    }

    [Fact]
    public void A_record_dated_to_the_send_day_names_no_date()
    {
        Echo.Compose(Record()).Text.Should().NotContain("Date:");
    }

    [Fact]
    public void A_cancelled_record_offers_only_restore()
    {
        var echo = Echo.Compose(Record(TransactionStatus.Cancelled));

        echo.Text.Should().StartWith("Cancelled — Cash\n• кофе");
        echo.Actions.Should().Equal(RecordAction.Restore);
    }

    [Fact]
    public void A_read_that_found_nothing_says_so_and_offers_only_edit()
    {
        var echo = Echo.Compose(Record(lines: []));

        echo.Text.Should().Be("Cash: found no spending here — nothing recorded.");
        echo.Actions.Should().Equal(RecordAction.Edit);
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
            + "Recorded — Cash\n• кофе — 250.00 RSD · Food & Drink\n\nTotal: 250.00 RSD");
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
        new(Guid.NewGuid(), heard, 111L, 42, "Cash", status, Sent, Sent, lines ?? [Coffee], CaptureKind.Voice);

    [Fact]
    public void Transcribing_is_the_voice_notes_acknowledgement()
    {
        Echo.Transcribing.Should().Be("🎤 Transcribing…");
    }

    [Fact]
    public void A_voice_record_starts_with_what_was_heard()
    {
        var echo = Echo.Compose(Voice("кофе двести пятьдесят"));

        echo.Text.Should().Be("🎤 \"кофе двести пятьдесят\"\nRecorded — Cash\n• кофе — 250.00 RSD · Food & Drink\n\nTotal: 250.00 RSD");
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
        Echo.Compose(Voice("кофе 250", status: TransactionStatus.Cancelled)).Text.Should().StartWith("🎤 \"кофе 250\"\nCancelled — Cash");
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

        echo.Text.Should().Be("Heard nothing in that voice note.\n\nRecorded — Cash\n• кофе — 250.00 RSD · Food & Drink\n\nTotal: 250.00 RSD");
        echo.Actions.Should().Equal(RecordAction.Cancel, RecordAction.Edit);
    }
}
