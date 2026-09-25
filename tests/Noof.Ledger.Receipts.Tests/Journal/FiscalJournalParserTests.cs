using AwesomeAssertions;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Receipts.Journal;

namespace Noof.Ledger.Receipts.Tests.Journal;

public class FiscalJournalParserTests
{
    const string CyrillicCardJournal = """
        ============ ФИСКАЛНИ РАЧУН ============
        123456789
        ТЕСТ ДОО
        Тест продавница
        001-Продавница Центар
        Кнез Михаилова 1
        Београд
        ========================================
        Хлеб/kom(Ђ)
              120,00               2       240,00
        Млеко(Е)
               89,90               1        89,90
        ----------------------------------------
        Укупан износ: 329,90
        Платна картица: 329,90
        ========================================
        Ђ  ОПШТА  20                    40,00
        ----------------------------------------
        Укупан износ пореза: 40,00
        ========================================
        ПФР време: 25.09.2026. 12:30:00
        ПФР број рачуна: ЈИД123-АБВ456-78
        Бројач рачуна: 123/456ПП
        ========================================
        data:image/gif;base64,AAAA
        ======== КРАЈ ФИСКАЛНОГ РАЧУНА =========
        """;

    const string LatinMixedRefundJournal = """
        ======== KOPIJA FISKALNOG RAČUNA =======
        987654321
        TEST DOO
        Prodavnica Jug
        002-Prodajno mesto Jug
        Bulevar Oslobođenja 10
        Novi Sad
        ========================================
        Mleko/kom(Đ)
               99,00               1        99,00
        Hleb(E)
              150,00               2       300,00
        ----------------------------------------
        Ukupna refundacija: 399,00
        Platna kartica: 200,00
        Gotovina: 199,00
        ========================================
        Đ  OPŠTA  20                     66,50
        ----------------------------------------
        Ukupan iznos poreza: 66,50
        ========================================
        PFR vreme: 03.02.2026. 09:15:42
        PFR broj računa: ABC999-XYZ111-22
        Brojač računa: 55/66PP
        ========================================
        data:image/gif;base64,BBBB
        ======== KRAJ FISKALNOG RAČUNA =========
        """;

    [Fact]
    public void Parses_a_cyrillic_card_journal()
    {
        var result = FiscalJournalParser.Parse(CyrillicCardJournal);

        result.Should().NotBeNull();
        result!.SellerTaxId.Should().Be("123456789");
        result.SellerName.Should().Be("ТЕСТ ДОО Тест продавница");
        result.LocationName.Should().Be("Продавница Центар");
        result.SellerAddress.Should().Be("Кнез Михаилова 1");
        result.FiscalNumber.Should().Be("ЈИД123-АБВ456-78");
        result.IssuedAt.Should().Be(BelgradeTime(2026, 9, 25, 12, 30, 0));
        result.Total.Should().Be(329.90m);
        result.PaymentMethod.Should().Be(PaymentMethod.Card);

        result.Lines.Should().HaveCount(2);
        result.Lines[0].Ordinal.Should().Be(1);
        result.Lines[0].Name.Should().Be("Хлеб");
        result.Lines[0].Unit.Should().Be("kom");
        result.Lines[0].TaxLabel.Should().Be("Ђ");
        result.Lines[0].UnitPrice.Should().Be(120.00m);
        result.Lines[0].Quantity.Should().Be(2m);
        result.Lines[0].Total.Should().Be(240.00m);

        result.Lines[1].Ordinal.Should().Be(2);
        result.Lines[1].Name.Should().Be("Млеко");
        result.Lines[1].Unit.Should().BeNull();
        result.Lines[1].TaxLabel.Should().Be("Е");
    }

    [Fact]
    public void Parses_a_latin_mixed_payment_refund_journal()
    {
        var result = FiscalJournalParser.Parse(LatinMixedRefundJournal);

        result.Should().NotBeNull();
        result!.SellerTaxId.Should().Be("987654321");
        result.SellerName.Should().Be("TEST DOO Prodavnica Jug");
        result.LocationName.Should().Be("Prodajno mesto Jug");
        result.SellerAddress.Should().Be("Bulevar Oslobođenja 10");
        result.FiscalNumber.Should().Be("ABC999-XYZ111-22");
        result.IssuedAt.Should().Be(BelgradeTime(2026, 2, 3, 9, 15, 42));
        result.Total.Should().Be(399.00m);
        result.PaymentMethod.Should().Be(PaymentMethod.Mixed);

        result.Lines.Should().HaveCount(2);
        result.Lines[0].Name.Should().Be("Mleko");
        result.Lines[0].Unit.Should().Be("kom");
        result.Lines[0].TaxLabel.Should().Be("Đ");
    }

    [Fact]
    public void Reports_printed_line_totals_without_correcting_them()
    {
        const string journal = """
            ============ ФИСКАЛНИ РАЧУН ============
            123456789
            ТЕСТ ДОО
            001-Продавница
            Адреса 1
            Град
            ========================================
            Артикал(Ђ)
                   10,00               3        99,00
            ----------------------------------------
            Укупан износ: 99,00
            Готовина: 99,00
            ========================================
            Ђ  ОПШТА  20                     16,50
            ----------------------------------------
            Укупан износ пореза: 16,50
            ========================================
            ПФР време: 01.01.2026. 00:00:00
            ПФР број рачуна: X-Y-Z
            Бројач рачуна: 1/1ПП
            ========================================
            data:image/gif;base64,AAAA
            ======== КРАЈ ФИСКАЛНОГ РАЧУНА =========
            """;

        var result = FiscalJournalParser.Parse(journal);

        result.Should().NotBeNull();
        result!.Lines.Single().UnitPrice.Should().Be(10.00m);
        result.Lines.Single().Quantity.Should().Be(3m);
        result.Lines.Single().Total.Should().Be(99.00m);
        result.PaymentMethod.Should().Be(PaymentMethod.Cash);
    }

    [Fact]
    public void Returns_null_for_text_with_no_recognisable_structure()
    {
        FiscalJournalParser.Parse("just some unrelated text").Should().BeNull();
    }

    static DateTimeOffset BelgradeTime(int year, int month, int day, int hour, int minute, int second)
    {
        var belgrade = TimeZoneInfo.FindSystemTimeZoneById("Europe/Belgrade");
        var local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, belgrade.GetUtcOffset(local));
    }
}
