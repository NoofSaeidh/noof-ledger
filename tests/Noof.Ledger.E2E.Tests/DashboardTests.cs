using System.Globalization;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence;

namespace Noof.Ledger.E2E.Tests;

public sealed class DashboardTests(CookieModeHostFixture fixture) : PageTest, IClassFixture<CookieModeHostFixture>
{
    // From migration AddCaptureModel's seed SQL - present in every clone because the clone is
    // created FROM the already-migrated template database, not migrated fresh by this fixture
    // (CookieModeHostFixture starts the host with Database__MigrateOnStartup=false).
    static readonly Guid DefaultWalletId = new("00000000-0000-0000-0000-000000000001");
    static readonly Guid CoffeeCategoryId = new("00000000-0000-0000-0001-000000000017");

    [Fact]
    public async Task Dashboard_shows_a_categorised_transaction_and_its_month_total()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var cancellationToken = TestContext.Current.CancellationToken;
        var description = $"кофе {Guid.NewGuid():N}";
        var transactionId = Guid.NewGuid();
        var merchantId = Guid.NewGuid();
        var chatId = DateTimeOffset.UtcNow.Ticks;

        await using (var db = OpenDb())
        {
            db.Merchants.Add(new Merchant { Id = merchantId, DisplayName = "Kafeterija", Kind = MerchantKind.Retail });
            db.Transactions.Add(new Transaction
            {
                Id = transactionId,
                WalletId = DefaultWalletId,
                RawText = $"{description} 250 рсд",
                Status = TransactionStatus.Completed,
                TimeZoneId = "Europe/Belgrade",
                OccurredAt = DateTimeOffset.UtcNow,
                OccurredOn = ZonedClock.LocalDate(DateTimeOffset.UtcNow, "Europe/Belgrade"),
                TelegramChatId = chatId,
                TelegramMessageId = 1,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.LineItems.Add(new LineItem
            {
                Id = Guid.NewGuid(),
                TransactionId = transactionId,
                Description = description,
                Amount = new Money(250.00m, CurrencyCode.Rsd),
                CategoryId = CoffeeCategoryId,
                CategorizedBy = CategorizationAuthority.Model,
                MerchantId = merchantId,
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var article = Page.Locator($"#txn-{transactionId}");
        var amountText = 250.00m.ToString("N2", CultureInfo.InvariantCulture);

        await Expect(article).ToContainTextAsync(description);
        await Expect(article).ToContainTextAsync("Kafeterija");
        await Expect(article).ToContainTextAsync("Main Wallet");
        await Expect(article).ToContainTextAsync(amountText);
        await AssertContainsCoffeeCategoryAsync(article);

        var monthTotals = Page.Locator("#month-totals-RSD");
        await Expect(monthTotals).ToContainTextAsync(amountText);
        await AssertContainsCoffeeCategoryAsync(monthTotals);
    }

    [Fact]
    public async Task Awaiting_and_failed_transactions_render_distinct_states_not_an_empty_result()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var cancellationToken = TestContext.Current.CancellationToken;
        var awaitingId = Guid.NewGuid();
        var failedId = Guid.NewGuid();
        var chatId = DateTimeOffset.UtcNow.Ticks;

        await using (var db = OpenDb())
        {
            db.Transactions.Add(NewTransaction(awaitingId, chatId, 1, TransactionStatus.Captured));
            db.Transactions.Add(NewTransaction(failedId, chatId, 2, TransactionStatus.Failed));
            await db.SaveChangesAsync(cancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Expect(Page.Locator($"#txn-{awaitingId}")).ToContainTextAsync("Awaiting categorisation");
        await Expect(Page.Locator($"#txn-{failedId}")).ToContainTextAsync("Categorisation failed");
    }

    // CategoryName is Task 6's EfSpendingReadModel picking between the category's bilingual NameEn /
    // NameRu (P1-1) - nothing in the Task 1 contract or in docs/OPEN-QUESTIONS.md commits to which
    // one, so this test accepts either rather than baking in an assumption Task 6 hasn't made yet.
    static async Task AssertContainsCoffeeCategoryAsync(ILocator scope)
    {
        var text = await scope.TextContentAsync() ?? string.Empty;
        // The English name, not the Russian one: EfSpendingReadModel resolves categories.name_en
        // (Task 6), because the settled rule is that the interface is English and only model-authored
        // prose is Russian. Asserting "either name" would let a read model that silently switched
        // languages pass this test.
        text.Contains("Coffee", StringComparison.Ordinal)
            .Should().BeTrue("the coffee category's display name must appear, in whichever of NameEn/NameRu the read model chooses");
    }

    LedgerDbContext OpenDb() =>
        new(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(fixture.ConnectionString).Options);

    static Transaction NewTransaction(Guid id, long chatId, int messageId, TransactionStatus status) => new()
    {
        Id = id,
        WalletId = DefaultWalletId,
        RawText = $"seeded {status} transaction {id:N}",
        Status = status,
        TimeZoneId = "Europe/Belgrade",
        OccurredAt = DateTimeOffset.UtcNow,
        OccurredOn = ZonedClock.LocalDate(DateTimeOffset.UtcNow, "Europe/Belgrade"),
        TelegramChatId = chatId,
        TelegramMessageId = messageId,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    async Task SignInAsync()
    {
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForURLAsync("**/account/login*");
        await Page.FillAsync("input[name='username']", CookieModeHostFixture.Username);
        await Page.FillAsync("input[name='password']", CookieModeHostFixture.Password);
        await Page.ClickAsync("button[type='submit']");
        await Page.WaitForURLAsync(fixture.BaseUrl + "/");
    }
}
