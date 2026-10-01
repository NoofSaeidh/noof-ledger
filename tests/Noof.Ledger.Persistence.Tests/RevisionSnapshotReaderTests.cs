using AwesomeAssertions;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Revisions;

namespace Noof.Ledger.Persistence.Tests;

public class RevisionSnapshotReaderTests
{
    static readonly Guid Wise = new("7a1c0000-0000-4000-8000-000000000001");
    static readonly Guid Cash = new("7a1c0000-0000-4000-8000-000000000002");
    static readonly Guid Kaspi = new("7a1c0000-0000-4000-8000-000000000003");

    // Exactly what RevisionLog wrote before Phase 4: no kind and no wallet either.
    const string BeforePhase4 = """
        {"raw_text":"кофе 250","occurred_on":"2026-09-25","items":[{"description":"кофе","amount":"250.0000","currency":"RSD","category_slug":"coffee","merchant_id":null,"categorized_by":1}]}
        """;

    // Exactly what RevisionLog wrote before Phase 7: no item role, no transfer, no charges.
    const string BeforePhase7 = """
        {"raw_text":"кофе 250","occurred_on":"2026-09-25","items":[{"description":"кофе","amount":"250.0000","currency":"RSD","category_slug":"coffee","merchant_id":null,"categorized_by":1}],"kind":"Expense","wallet_id":"00000000-0000-0000-0000-000000000001","stated_balance":null}
        """;

    const string Exchange = """
        {"raw_text":"поменял 100 евро по 117.35","occurred_on":"2026-09-25","items":[{"description":"Fee","amount":"1.0000","currency":"EUR","category_slug":"fees-charges","merchant_id":null,"categorized_by":2,"role":1}],"kind":"Transfer","wallet_id":"7a1c0000-0000-4000-8000-000000000001","stated_balance":null,"transfer":{"from_wallet_id":"7a1c0000-0000-4000-8000-000000000001","from_amount":"101.0000","from_currency":"EUR","to_wallet_id":"7a1c0000-0000-4000-8000-000000000002","to_amount":"11735.0000","to_currency":"RSD","fee_leg":0,"stated_rate":"117.350000000000","stated_rate_base":"EUR","venue_merchant_id":null},"charges":[]}
        """;

    const string ForeignSpending = """
        {"raw_text":"30 долларов с каспи","occurred_on":"2026-09-25","items":[{"description":"App Store","amount":"30.0000","currency":"USD","category_slug":"subscriptions","merchant_id":null,"categorized_by":1,"role":0},{"description":"Fee · USD purchase","amount":"156.0000","currency":"KZT","category_slug":"fees-charges","merchant_id":null,"categorized_by":2,"role":1}],"kind":"Expense","wallet_id":"7a1c0000-0000-4000-8000-000000000003","stated_balance":null,"transfer":null,"charges":[{"currency":"USD","charged_amount":"15600.0000","fee_amount":"156.0000","rate_used":"520.000000000000","fee_percent":"1.0000","fee_fixed":null,"fee_minimum":null,"source":0}]}
        """;

    [Fact]
    public void Reads_a_snapshot_written_before_phase_4_with_no_kind_and_no_wallet()
    {
        RevisionSnapshotReader.Read(BeforePhase4).Should().BeEquivalentTo(new ParsedSnapshot(
            null,
            null,
            [new ParsedItem("кофе", new Money(250m, CurrencyCode.Rsd), "coffee", EntryRole.Principal)],
            null,
            []));
    }

    [Fact]
    public void Reads_a_snapshot_written_before_phase_7_as_principal_items_with_no_transfer_and_no_charges()
    {
        RevisionSnapshotReader.Read(BeforePhase7).Should().BeEquivalentTo(new ParsedSnapshot(
            TransactionKind.Expense,
            new Guid("00000000-0000-0000-0000-000000000001"),
            [new ParsedItem("кофе", new Money(250m, CurrencyCode.Rsd), "coffee", EntryRole.Principal)],
            null,
            []));
    }

    [Fact]
    public void Reads_a_transfer_with_its_legs_its_fee_leg_and_its_stated_rate()
    {
        RevisionSnapshotReader.Read(Exchange).Should().BeEquivalentTo(new ParsedSnapshot(
            TransactionKind.Transfer,
            Wise,
            [new ParsedItem("Fee", new Money(1m, CurrencyCode.Eur), "fees-charges", EntryRole.Fee)],
            new ParsedTransfer(
                Wise, new Money(101m, CurrencyCode.Eur), Cash, new Money(11735m, CurrencyCode.Rsd),
                TransferLeg.From, 117.35m, CurrencyCode.Eur, null),
            []));
    }

    [Fact]
    public void Reads_a_foreign_spending_with_its_charge_and_its_terms()
    {
        RevisionSnapshotReader.Read(ForeignSpending).Should().BeEquivalentTo(new ParsedSnapshot(
            TransactionKind.Expense,
            Kaspi,
            [
                new ParsedItem("App Store", new Money(30m, CurrencyCode.Usd), "subscriptions", EntryRole.Principal),
                new ParsedItem("Fee · USD purchase", new Money(156m, CurrencyCode.Kzt), "fees-charges", EntryRole.Fee),
            ],
            null,
            [new ParsedCharge(CurrencyCode.Usd, 15600m, 156m, 520m, 1m, null, null, ChargeSource.WalletTerms)]));
    }

    [Fact]
    public void A_damaged_snapshot_throws_instead_of_reading_as_zero()
    {
        var act = () => RevisionSnapshotReader.Read("""
            {"kind":"Expense","items":[{"description":"кофе","currency":"RSD"}]}
            """);

        act.Should().Throw<KeyNotFoundException>("an item without an amount is damage, not a zero");
    }

    [Fact]
    public void An_empty_snapshot_reads_as_nothing()
    {
        RevisionSnapshotReader.Read("{}").Should().BeNull(
            "revisions seeded with an empty object have no record to show, and must not break the history");
    }
}
