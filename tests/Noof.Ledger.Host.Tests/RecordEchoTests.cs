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

    static RecordedLine Coffee => new("кофе", new Money(250m, CurrencyCode.Rsd), "food-drink", "Еда и напитки", null);

    static CategorizationSubject Record(
        TransactionStatus status = TransactionStatus.Completed, DateOnly? occurredOn = null,
        IReadOnlyList<RecordedLine>? lines = null) =>
        new(Guid.NewGuid(), "raw", 111L, 42, "Cash", status, Sent, occurredOn ?? Sent, lines ?? [Coffee]);

    [Fact]
    public void A_recorded_line_is_echoed_with_its_total_and_the_cancel_and_edit_buttons()
    {
        var echo = Echo.Compose(Record());

        echo.Text.Should().Be("Записал — Cash\n• кофе — 250.00 RSD · Еда и напитки\n\nИтого: 250.00 RSD");
        echo.Actions.Should().Equal(RecordAction.Cancel, RecordAction.Edit);
    }

    [Fact]
    public void A_merchant_follows_the_category()
    {
        var line = new RecordedLine("продукты", new Money(1000m, CurrencyCode.Eur), "groceries", "Продукты", "Lidl");

        Echo.Compose(Record(lines: [line])).Text.Should().Contain("• продукты — 1000.00 EUR · Продукты · Lidl");
    }

    [Fact]
    public void Totals_are_per_currency_and_ordered_by_code()
    {
        var lines = new[]
        {
            Coffee,
            new RecordedLine("такси", new Money(1000m, CurrencyCode.Eur), "transport", "Транспорт", null),
            new RecordedLine("хлеб", new Money(100m, CurrencyCode.Rsd), "groceries", "Продукты", null),
        };

        Echo.Compose(Record(lines: lines)).Text.Should().EndWith("Итого: 1000.00 EUR, 350.00 RSD");
    }

    [Fact]
    public void A_record_dated_to_another_day_says_which_day()
    {
        Echo.Compose(Record(occurredOn: new DateOnly(2026, 9, 21))).Text
            .Should().StartWith("Записал — Cash\nДата: 21.09.2026\n• кофе");
    }

    [Fact]
    public void A_record_dated_to_the_send_day_names_no_date()
    {
        Echo.Compose(Record()).Text.Should().NotContain("Дата:");
    }

    [Fact]
    public void A_cancelled_record_offers_only_restore()
    {
        var echo = Echo.Compose(Record(TransactionStatus.Cancelled));

        echo.Text.Should().StartWith("Отменено — Cash\n• кофе");
        echo.Actions.Should().Equal(RecordAction.Restore);
    }

    [Fact]
    public void A_read_that_found_nothing_says_so_and_offers_only_edit()
    {
        var echo = Echo.Compose(Record(lines: []));

        echo.Text.Should().Be("Cash: не нашёл здесь трат — ничего не записал.");
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
            "Не получилось применить исправление — запись не изменилась.\n\n"
            + "Записал — Cash\n• кофе — 250.00 RSD · Еда и напитки\n\nИтого: 250.00 RSD");
        echo.Actions.Should().Equal(RecordAction.Cancel, RecordAction.Edit);
    }

    [Fact]
    public void A_line_with_no_category_says_so()
    {
        var line = new RecordedLine("штраф", new Money(5m, CurrencyCode.Eur), null, null, null);

        Echo.Compose(Record(lines: [line])).Text.Should().Contain("• штраф — 5.00 EUR · без категории");
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
}
