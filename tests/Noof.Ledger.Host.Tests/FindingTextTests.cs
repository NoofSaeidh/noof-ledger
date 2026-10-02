using System.Globalization;
using AwesomeAssertions;
using Noof.Ledger.Application.Diagnostics.Integrity;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Host.Tests;

public class FindingTextTests
{
    static readonly FindingText Text = new();
    static readonly DateTimeOffset AsOf = new(2026, 10, 2, 14, 19, 0, TimeSpan.Zero);
    static readonly Guid RecordId = Guid.Parse("7a1c0000-0000-4000-8000-000000000900");
    static readonly Guid WalletId = Guid.Parse("7a1c0000-0000-4000-8000-0000000000a1");

    static IntegrityFinding Disagreement() =>
        new(IntegrityCheck.PostingsDisagree, IntegrityGroup.Bug, RecordId, WalletId, null,
        [
            new TextFact("Wallet", "Cash RSD"),
            new TextFact("Role", "Principal"),
            new MoneyFact("Expected", -250m, CurrencyCode.Rsd),
            new MoneyFact("Posted", -200m, CurrencyCode.Rsd),
        ]);

    static IntegrityFinding WaitingForRecordAnyway() =>
        new(IntegrityCheck.NotApplied, IntegrityGroup.WaitingOnYou, RecordId, null, null,
        [
            new TextFact("Waiting for", "Record anyway"),
            new TextFact("Status", "Captured"),
            new SinceFact("Idle for", new DateTimeOffset(2026, 9, 30, 11, 4, 0, TimeSpan.Zero)),
            new DateFact("Date", new DateOnly(2026, 9, 18)),
            new MoneyFact("Amount", 1840.5m, CurrencyCode.Rsd),
        ]);

    static string[] DisagreementLines(int number) =>
    [
        $"{number}. Postings disagree with their sources (Bug)",
        "   Wallet: Cash RSD",
        "   Role: Principal",
        "   Expected: -250.00 RSD",
        "   Posted: -200.00 RSD",
    ];

    static string[] WaitingLines(int number) =>
    [
        $"{number}. Not applied (Waiting on you)",
        "   Waiting for: Record anyway",
        "   Status: Captured",
        "   Idle for: 2 d 3 h (since 2026-09-30 11:04 UTC)",
        "   Date: 2026-09-18",
        "   Amount: 1840.50 RSD",
    ];

    [Theory]
    [InlineData(IntegrityCheck.PostingsDisagree, "Postings disagree with their sources",
        "A record's ledger entries differ from what its lines, charges and transfer legs add up to, sit on a wallet the record does not use, or a foreign charge is priced differently from the lines it prices. The wallet's balance is then wrong. It usually means the app wrote the entries incorrectly — a bug in the app.")]
    [InlineData(IntegrityCheck.FactsMismatchKind, "A record's facts do not match its kind",
        "A record's stored facts contradict its kind: a transfer without its legs or with a spending line, a statement without its checkpoint, a foreign charge on something that is not a spending or in the wallet's own currency, a fee leg that does not match its fee line, a leg or checkpoint on the wrong wallet or currency, or a failure reason on a record that is not failed. It usually means the app wrote the record inconsistently — a bug in the app.")]
    [InlineData(IntegrityCheck.StuckInPipeline, "Stuck in the pipeline",
        "A message was captured more than 10 minutes ago and nothing is working on it: no job is queued, running or failed, and it is not waiting for Record anyway. It usually means the app never queued the work for it — a bug in the app.")]
    [InlineData(IntegrityCheck.NotApplied, "Not applied",
        "Something has waited on the operator for more than a day: a record whose first reading failed (the bot asked for something, or could not read it), a receipt or exchange slip waiting for Record anyway, a correction, re-read or transcription that never applied, or a record restored after a cancel that nothing will process. The operator answers by replying to the echo with what is missing, pressing Record anyway, or cancelling the record.")]
    public void Each_check_has_its_fixed_title_and_description(IntegrityCheck check, string title, string description)
    {
        Text.Title(check).Should().Be(title);
        Text.Description(check).Should().Be(description);
    }

    [Theory]
    [InlineData(IntegrityGroup.Bug, "Bug")]
    [InlineData(IntegrityGroup.WaitingOnYou, "Waiting on you")]
    public void Each_group_has_its_label(IntegrityGroup group, string label) =>
        Text.GroupLabel(group).Should().Be(label);

    [Theory]
    [InlineData("-250", "-250.00 RSD")]
    [InlineData("-250.0000", "-250.00 RSD")]
    [InlineData("12000.5", "12000.50 RSD")]
    [InlineData("0", "0.00 RSD")]
    public void A_money_fact_prints_two_decimals_and_its_currency(string amount, string expected) =>
        Text.FormatValue(new MoneyFact("Expected", decimal.Parse(amount, CultureInfo.InvariantCulture), CurrencyCode.Rsd), AsOf)
            .Should().Be(expected);

    [Fact]
    public void A_money_fact_ignores_the_current_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
        try
        {
            Text.FormatValue(new MoneyFact("Expected", -12000.5m, CurrencyCode.Eur), AsOf).Should().Be("-12000.50 EUR");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Date_count_and_text_facts_print_as_iso_day_invariant_integer_and_as_is()
    {
        Text.FormatValue(new DateFact("Date", new DateOnly(2026, 9, 18)), AsOf).Should().Be("2026-09-18");
        Text.FormatValue(new CountFact("Fee lines", 2), AsOf).Should().Be("2");
        Text.FormatValue(new TextFact("Wallet", "Cash RSD · «main»"), AsOf).Should().Be("Cash RSD · «main»");
    }

    [Theory]
    [InlineData("2026-09-30T11:04:00+00:00", "2 d 3 h (since 2026-09-30 11:04 UTC)")]
    [InlineData("2026-10-01T14:19:00+00:00", "1 d 0 h (since 2026-10-01 14:19 UTC)")]
    [InlineData("2026-10-01T16:19:00+02:00", "1 d 0 h (since 2026-10-01 14:19 UTC)")]
    [InlineData("2026-10-02T13:14:00+00:00", "1 h 5 min (since 2026-10-02 13:14 UTC)")]
    [InlineData("2026-10-02T13:19:01+00:00", "59 min (since 2026-10-02 13:19 UTC)")]
    [InlineData("2026-10-02T14:19:00+00:00", "0 min (since 2026-10-02 14:19 UTC)")]
    public void A_since_fact_prints_its_age_in_whole_units_and_its_start_in_utc(string since, string expected) =>
        Text.FormatValue(new SinceFact("Idle for", DateTimeOffset.Parse(since, CultureInfo.InvariantCulture)), AsOf)
            .Should().Be(expected);

    [Fact]
    public void A_wait_that_starts_after_as_of_is_age_zero()
    {
        Text.FormatValue(new SinceFact("Idle for", AsOf.AddMinutes(5)), AsOf).Should().Be("0 min (since 2026-10-02 14:24 UTC)");
        Text.FormatValue(new SinceFact("Idle for", AsOf.AddDays(3)), AsOf).Should().Be("0 min (since 2026-10-05 14:19 UTC)");
    }

    [Fact]
    public void Format_puts_the_name_before_the_value()
    {
        Text.Format(new MoneyFact("Expected", -250m, CurrencyCode.Rsd), AsOf).Should().Be("Expected: -250.00 RSD");
        Text.Format(new SinceFact("Idle for", new DateTimeOffset(2026, 9, 30, 11, 4, 0, TimeSpan.Zero)), AsOf)
            .Should().Be("Idle for: 2 d 3 h (since 2026-09-30 11:04 UTC)");
    }

    [Fact]
    public void The_findings_block_numbers_each_finding_with_its_group_and_indents_its_facts_in_order()
    {
        string[] expected = [.. DisagreementLines(1), .. WaitingLines(2)];

        Text.FindingsBlock([Disagreement(), WaitingForRecordAnyway()], AsOf).Should().Be(string.Join("\n", expected));
    }

    [Fact]
    public void No_findings_make_an_empty_block() =>
        Text.FindingsBlock([], AsOf).Should().BeEmpty();
}
