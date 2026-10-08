using System.Text.Json;
using AwesomeAssertions;
using Noof.Ledger.Application.Fx;
using Noof.Ledger.Domain;
using Noof.Ledger.TestKit;

namespace Noof.Ledger.Fx.Tests;

public class OpenErApiPayloadTests
{
    // 2026-10-08 12:00:00 UTC.
    static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    // 2026-10-09 12:00:00 UTC: Now plus the one day of clock difference the source tolerates.
    const long OneDayAheadUnix = 1_791_547_200;

    static (FxRateSnapshot? Snapshot, string? Rejection) Read(string json) =>
        (JsonSerializer.Deserialize<OpenErApiPayload>(json)
            ?? throw new InvalidOperationException("The test payload read as JSON null.")).Read(Now);

    static FxRateSnapshot Accepted(string json)
    {
        var (snapshot, rejection) = Read(json);
        rejection.Should().BeNull();
        return snapshot.Should().BeOfType<FxRateSnapshot>().Subject;
    }

    [Fact]
    public void Keeps_every_supported_currency_but_EUR_and_nothing_else()
    {
        var snapshot = Accepted(OpenErApiPayloads.Latest());

        snapshot.Source.Should().Be(FxSources.OpenErApi);
        snapshot.UnitsPerEur.Should().BeEquivalentTo(new Dictionary<CurrencyCode, decimal>
        {
            [CurrencyCode.Rsd] = 117.1532m,
            [CurrencyCode.Usd] = 1.1612m,
            [CurrencyCode.Rub] = 94.3121m,
            [CurrencyCode.Kzt] = 625.4417m,
        }, "EUR is 1 by definition and GBP is not a supported currency");
    }

    [Fact]
    public void The_as_of_date_is_the_UTC_date_of_the_update()
    {
        Accepted(OpenErApiPayloads.Latest()).AsOfDate.Should().Be(new DateOnly(2026, 10, 8));

        // 23:30 UTC on the 7th is already 01:30 on the 8th in Belgrade; the rate is still the 7th's.
        Accepted(OpenErApiPayloads.Latest(updatedUnix: 1_791_415_800)).AsOfDate.Should().Be(new DateOnly(2026, 10, 7));
    }

    // 16 significant digits: a double keeps 15-17 and decimal(double) keeps 15, so a read through double would give
    // 625.441712345679 instead.
    [Fact]
    public void A_rate_is_read_as_a_decimal_digit_for_digit()
    {
        var snapshot = Accepted(OpenErApiPayloads.Latest(rates: OpenErApiPayloads.RatesWith("KZT", "625.4417123456789")));

        snapshot.UnitsPerEur[CurrencyCode.Kzt].Should().Be(625.4417123456789m);
    }

    [Fact]
    public void The_largest_rate_numeric_24_12_holds_is_kept()
    {
        var snapshot = Accepted(OpenErApiPayloads.Latest(rates: OpenErApiPayloads.RatesWith("KZT", "999999999999.999999999999")));

        snapshot.UnitsPerEur[CurrencyCode.Kzt].Should().Be(999_999_999_999.999_999_999_999m);
    }

    [Fact]
    public void An_update_exactly_one_day_ahead_of_the_clock_is_kept()
    {
        Accepted(OpenErApiPayloads.Latest(updatedUnix: OneDayAheadUnix)).AsOfDate.Should().Be(new DateOnly(2026, 10, 9));
    }

    public static TheoryData<string, string> RejectedPayloads() => new()
    {
        { """{"result":"error","error-type":"unsupported-code"}""", "the result was not success" },
        { OpenErApiPayloads.Latest(result: "maintenance"), "the result was not success" },
        { OpenErApiPayloads.Latest(baseCode: "USD"), "the base was not EUR" },
        { OpenErApiPayloads.Latest(updatedUnix: null), "the update time is missing" },
        { OpenErApiPayloads.Latest(updatedUnix: 0), "the update time is missing" },
        { OpenErApiPayloads.Latest(updatedUnix: OneDayAheadUnix + 1), "the update time is in the future" },
        { OpenErApiPayloads.Latest(updatedUnix: 99_999_999_999_999), "the update time is in the future" },
        { """{"result":"success","base_code":"EUR","time_last_update_unix":1791417601,"time_eol_unix":0}""", "the rates are missing" },
        { OpenErApiPayloads.Latest(rates: OpenErApiPayloads.RatesWith("RSD", null)), "the RSD rate is missing" },
        { OpenErApiPayloads.Latest(rates: OpenErApiPayloads.RatesWith("RSD", "0")), "the RSD rate is not above zero" },
        { OpenErApiPayloads.Latest(rates: OpenErApiPayloads.RatesWith("RSD", "-117.1532")), "the RSD rate is not above zero" },
        { OpenErApiPayloads.Latest(rates: OpenErApiPayloads.RatesWith("KZT", "1000000000000")), "the KZT rate does not fit numeric(24,12)" },
        { OpenErApiPayloads.Latest(rates: OpenErApiPayloads.RatesWith("KZT", "999999999999.9999999999995")), "the KZT rate does not fit numeric(24,12)" },
        { OpenErApiPayloads.Latest(rates: OpenErApiPayloads.RatesWith("USD", "0.0000000000004")), "the USD rate does not fit numeric(24,12)" },
    };

    [Theory]
    [MemberData(nameof(RejectedPayloads))]
    public void An_invalid_payload_gives_no_snapshot_and_names_why(string json, string reason)
    {
        var (snapshot, rejection) = Read(json);

        snapshot.Should().BeNull("a snapshot is stored only whole, so nothing of an invalid one may come back");
        rejection.Should().Be(reason);
    }
}
