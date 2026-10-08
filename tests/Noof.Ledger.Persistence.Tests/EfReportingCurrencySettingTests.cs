using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Settings;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfReportingCurrencySettingTests(PostgresFixture fixture)
{
    static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static EfReportingCurrencySetting SettingFor(LedgerDbContext db) => new(db, new FakeTimeProvider(Now));

    [Fact]
    public async Task With_nothing_saved_the_reporting_currency_is_EUR()
    {
        await using var db = await fixture.CreateMigratedContextAsync();

        (await SettingFor(db).GetAsync(Ct)).Should().Be(CurrencyCode.Eur);
    }

    [Fact]
    public async Task A_saved_currency_reads_back_and_a_second_save_replaces_it()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var setting = SettingFor(db);

        await setting.SaveAsync(CurrencyCode.Rsd, Ct);
        (await setting.GetAsync(Ct)).Should().Be(CurrencyCode.Rsd);

        await setting.SaveAsync(CurrencyCode.Eur, Ct);
        (await setting.GetAsync(Ct)).Should().Be(CurrencyCode.Eur);
        (await db.AppSettings.AsNoTracking().SingleAsync(row => row.Key == "reporting.currency", Ct)).Value.Should().Be("EUR");
    }

    [Theory]
    [InlineData("USD")]
    [InlineData("rsd")]
    [InlineData("")]
    public async Task A_stored_value_that_is_not_EUR_or_RSD_reads_as_EUR(string stored)
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO app_setting (key, value, updated_at) VALUES ('reporting.currency', {stored}, {Now})", Ct);

        (await SettingFor(db).GetAsync(Ct)).Should().Be(CurrencyCode.Eur);
    }

    [Fact]
    public async Task Saving_a_currency_other_than_EUR_or_RSD_throws_and_stores_nothing()
    {
        await using var db = await fixture.CreateMigratedContextAsync();

        var act = () => SettingFor(db).SaveAsync(CurrencyCode.Usd, Ct);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        (await db.AppSettings.AsNoTracking().AnyAsync(row => row.Key == "reporting.currency", Ct)).Should().BeFalse();
    }
}
