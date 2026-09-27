using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence;

namespace Noof.Ledger.E2E.Tests;

public sealed class WalletsTests(CookieModeHostFixture fixture) : PageTest, IClassFixture<CookieModeHostFixture>
{
    [Fact]
    public async Task Creating_a_wallet_with_an_opening_balance_lists_it()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var name = $"Wise EUR {Guid.NewGuid():N}";

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/wallets");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Page.FillAsync("#new-wallet-name", name);
        await Page.SelectOptionAsync("#new-wallet-currency", "EUR");
        await Page.FillAsync("#new-wallet-opening", "1500.50");
        await Page.FillAsync("#new-wallet-date", "2026-09-01");
        await Page.ClickAsync("#create-wallet");

        await Expect(Page.Locator("#wallets-saved")).ToContainTextAsync("Created");

        await using var db = OpenDb();
        var wallet = await db.Wallets.SingleAsync(w => w.Name == name, TestContext.Current.CancellationToken);
        wallet.Currency.Should().Be(CurrencyCode.Eur);

        // The wallet name lives in an <input value>, which Playwright's text-content locators
        // (HasText, ToContainTextAsync) never see - assert the value itself.
        await Expect(Page.Locator($"#wallet-name-{wallet.Id}")).ToHaveValueAsync(name);

        var checkpoint = await db.BalanceChecks.SingleAsync(
            bc => bc.WalletId == wallet.Id, TestContext.Current.CancellationToken);
        checkpoint.Stated.Should().Be(new Money(1500.50m, CurrencyCode.Eur),
            "the opening-balance field is culture-pinned to invariant, so a decimal point parses the same regardless of the server's own regional settings");
    }

    [Fact]
    public async Task Renaming_editing_aliases_making_default_and_archiving_a_wallet()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var originalName = $"Cash {Guid.NewGuid():N}";
        var renamedTo = $"Petty cash {Guid.NewGuid():N}";

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/wallets");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Page.FillAsync("#new-wallet-name", originalName);
        await Page.SelectOptionAsync("#new-wallet-currency", "RSD");
        await Page.FillAsync("#new-wallet-opening", "0");
        await Page.FillAsync("#new-wallet-date", "2026-09-01");
        await Page.ClickAsync("#create-wallet");
        await Expect(Page.Locator("#wallets-saved")).ToContainTextAsync("Created");

        Guid walletId;
        await using (var db = OpenDb())
        {
            walletId = (await db.Wallets.SingleAsync(
                w => w.Name == originalName, TestContext.Current.CancellationToken)).Id;
        }

        var row = Page.Locator($"#wallet-{walletId}");
        await Expect(row).ToBeVisibleAsync();

        await Page.FillAsync($"#wallet-name-{walletId}", renamedTo);
        await Page.ClickAsync($"#rename-{walletId}");
        await Expect(Page.Locator("#wallets-saved")).ToContainTextAsync("Renamed");
        // The renamed value lives in an <input value>, not text content - ToContainTextAsync never sees it.
        await Expect(Page.Locator($"#wallet-name-{walletId}")).ToHaveValueAsync(renamedTo);

        await Page.FillAsync($"#wallet-aliases-{walletId}", "нал, cash, налик");
        await Page.ClickAsync($"#save-aliases-{walletId}");
        await Expect(Page.Locator("#wallets-saved")).ToContainTextAsync("Aliases saved");

        await Page.ClickAsync($"#make-default-{walletId}");
        await Expect(Page.Locator("#wallets-saved")).ToContainTextAsync("Made default");
        await Expect(row).ToContainTextAsync("Default for RSD");

        await Page.ClickAsync($"#archive-{walletId}");
        await Expect(Page.Locator("#wallets-saved")).ToContainTextAsync("Archived");
        await Expect(row).ToContainTextAsync("Archived");

        // Setting one is refused server-side (EfWalletAdmin.SetPaymentDefaultAsync) once archived,
        // so the control that would silently no-op through it must not be offered any more either.
        await Expect(Page.Locator($"#wallet-payment-default-{walletId}")).ToHaveCountAsync(0);

        // This wallet was RSD's only default; archiving it leaves RSD with none, and the page must say
        // so (Task 4 finding 3 pairs the mapper's failure text with this warning).
        await Expect(Page.Locator("#wallets-warning")).ToContainTextAsync("RSD");

        await using var verify = OpenDb();
        var wallet = await verify.Wallets.AsNoTracking()
            .SingleAsync(w => w.Id == walletId, TestContext.Current.CancellationToken);
        wallet.Name.Should().Be(renamedTo);
        wallet.Aliases.Should().BeEquivalentTo(["нал", "cash", "налик"]);
        wallet.Archived.Should().BeTrue();
    }

    [Fact]
    public async Task Setting_a_card_default_on_one_wallet_then_another_moves_it()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var firstName = $"Card A {Guid.NewGuid():N}";
        var secondName = $"Card B {Guid.NewGuid():N}";

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/wallets");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Page.FillAsync("#new-wallet-name", firstName);
        await Page.SelectOptionAsync("#new-wallet-currency", "RSD");
        await Page.FillAsync("#new-wallet-opening", "0");
        await Page.FillAsync("#new-wallet-date", "2026-09-01");
        await Page.ClickAsync("#create-wallet");
        await Expect(Page.Locator("#wallets-saved")).ToContainTextAsync("Created");

        await Page.FillAsync("#new-wallet-name", secondName);
        await Page.SelectOptionAsync("#new-wallet-currency", "RSD");
        await Page.FillAsync("#new-wallet-opening", "0");
        await Page.FillAsync("#new-wallet-date", "2026-09-01");
        await Page.ClickAsync("#create-wallet");
        await Expect(Page.Locator("#wallets-saved")).ToContainTextAsync("Created");

        var firstId = await WaitForWalletIdAsync(firstName, TestContext.Current.CancellationToken);
        var secondId = await WaitForWalletIdAsync(secondName, TestContext.Current.CancellationToken);

        await Page.SelectOptionAsync($"#wallet-payment-default-{firstId}", "Card");
        await Expect(Page.Locator("#wallets-saved")).ToContainTextAsync("Payment default saved");

        // "Payment default saved" is the same text both times this test triggers it, so Playwright's
        // wait above can pass on the *previous* action's leftover text before this one actually
        // commits (the same race as WaitForWalletIdAsync's "Created" - see there). Reading the wallet
        // back with a bounded poll, rather than trusting the confirmation text alone, is immune to it.
        (await WaitForPaymentDefaultAsync(firstId, WalletPaymentDefault.Card, TestContext.Current.CancellationToken))
            .Should().Be(WalletPaymentDefault.Card);

        await Page.SelectOptionAsync($"#wallet-payment-default-{secondId}", "Card");
        await Expect(Page.Locator("#wallets-saved")).ToContainTextAsync("Payment default saved");

        (await WaitForPaymentDefaultAsync(secondId, WalletPaymentDefault.Card, TestContext.Current.CancellationToken))
            .Should().Be(WalletPaymentDefault.Card);
        (await WaitForPaymentDefaultAsync(firstId, null, TestContext.Current.CancellationToken))
            .Should().BeNull("only the second wallet must show the card default now");
    }

    // The same confirmation text ("Created {name}.", "Payment default saved.") is reused across
    // repeated actions in a test, so Playwright's wait for it can be satisfied by a PREVIOUS action's
    // still-visible text before the current one has actually committed - Setting_a_card_default_on_
    // one_wallet_then_another_moves_it flaked with "Sequence contains no elements" from exactly that
    // gap. Polling the database with a bounded wait, instead of reading it once right after the UI
    // wait, is immune to it regardless of which confirmation raced.
    async Task<Guid> WaitForWalletIdAsync(string name, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            await using var db = OpenDb();
            var wallet = await db.Wallets.AsNoTracking().SingleOrDefaultAsync(w => w.Name == name, cancellationToken);
            if (wallet is not null)
                return wallet.Id;

            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"No wallet named '{name}' appeared within the timeout.");

            await Task.Delay(100, cancellationToken);
        }
    }

    async Task<WalletPaymentDefault?> WaitForPaymentDefaultAsync(
        Guid walletId, WalletPaymentDefault? expected, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            await using var db = OpenDb();
            var current = (await db.Wallets.AsNoTracking()
                    .SingleAsync(w => w.Id == walletId, cancellationToken))
                .DefaultForPayment;

            if (current == expected || DateTime.UtcNow >= deadline)
                return current;

            await Task.Delay(100, cancellationToken);
        }
    }

    [Fact]
    public async Task An_empty_wallet_name_is_refused_inline()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/wallets");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Page.SelectOptionAsync("#new-wallet-currency", "USD");
        await Page.FillAsync("#new-wallet-opening", "10");
        await Page.FillAsync("#new-wallet-date", "2026-09-01");
        await Page.ClickAsync("#create-wallet");

        await Expect(Page.Locator("#wallets-error")).ToContainTextAsync("Enter a wallet name.");
        await Expect(Page.Locator("#wallets-saved")).Not.ToBeVisibleAsync();
    }

    LedgerDbContext OpenDb() =>
        new(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(fixture.ConnectionString).Options);

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
