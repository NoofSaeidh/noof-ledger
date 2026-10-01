using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;
using Noof.Ledger.Application.Wallets;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence;
using Noof.Ledger.Persistence.Wallets;

namespace Noof.Ledger.E2E.Tests;

public sealed class WalletsTests(CookieModeHostFixture fixture) : PageTest, IClassFixture<CookieModeHostFixture>
{
    static readonly string[] OfferedToKzt = ["EUR", "RSD", "RUB", "USD"];
    static readonly string[] OfferedToKztWithUsdTerms = ["EUR", "RSD", "RUB"];

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
        await Expect(Page.Locator($"#terms-{walletId}")).ToBeVisibleAsync();

        await Page.ClickAsync($"#archive-{walletId}");
        await Expect(Page.Locator("#wallets-saved")).ToContainTextAsync("Archived");
        await Expect(row).ToContainTextAsync("Archived");

        // Setting one is refused server-side (EfWalletAdmin.SetPaymentDefaultAsync) once archived,
        // so the control that would silently no-op through it must not be offered any more either.
        await Expect(Page.Locator($"#wallet-payment-default-{walletId}")).ToHaveCountAsync(0);
        // Nor foreign-currency terms: an archived wallet is never charged for anything again.
        await Expect(Page.Locator($"#terms-{walletId}")).ToHaveCountAsync(0);

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

    [Fact]
    public async Task Adding_editing_and_removing_foreign_currency_terms()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var name = $"Kaspi KZT {Guid.NewGuid():N}";

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/wallets");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        var walletId = await CreateWalletAsync(name, "KZT");

        var offered = Page.Locator($"#terms-new-currency-{walletId} option");
        await Expect(offered).ToHaveTextAsync(OfferedToKzt);

        await Page.SelectOptionAsync($"#terms-new-currency-{walletId}", "USD");
        await Page.FillAsync($"#terms-new-rate-{walletId}", "520");
        await Page.FillAsync($"#terms-new-fee-percent-{walletId}", "1");
        await Page.FillAsync($"#terms-new-fee-minimum-{walletId}", "100");
        await Page.ClickAsync($"#terms-add-{walletId}");
        await Expect(Page.Locator("#wallets-saved")).ToContainTextAsync("USD terms added.");

        (await TermsOfAsync(walletId)).Should().Equal(new WalletTermsDetails(CurrencyCode.Usd, 520m, 1m, null, 100m));
        await Expect(Page.Locator($"#terms-row-{walletId}-USD")).ToBeVisibleAsync();
        // The values live in <input value>, which text locators never see. A rate stored as 520.000000000000 must
        // read back as the operator typed it.
        await Expect(Page.Locator($"#terms-rate-{walletId}-USD")).ToHaveValueAsync("520");
        await Expect(Page.Locator($"#terms-fee-percent-{walletId}-USD")).ToHaveValueAsync("1");
        await Expect(Page.Locator($"#terms-fee-fixed-{walletId}-USD")).ToHaveValueAsync("");
        await Expect(Page.Locator($"#terms-fee-minimum-{walletId}-USD")).ToHaveValueAsync("100.00");
        await Expect(offered).ToHaveTextAsync(OfferedToKztWithUsdTerms);

        await Page.FillAsync($"#terms-rate-{walletId}-USD", "515.5");
        await Page.FillAsync($"#terms-fee-fixed-{walletId}-USD", "50");
        await Page.ClickAsync($"#terms-save-{walletId}-USD");
        await Expect(Page.Locator("#wallets-saved")).ToContainTextAsync("USD terms saved.");

        (await TermsOfAsync(walletId)).Should().Equal(new WalletTermsDetails(CurrencyCode.Usd, 515.5m, 1m, 50m, 100m));

        await Page.ClickAsync($"#terms-remove-{walletId}-USD");
        await Expect(Page.Locator("#wallets-saved")).ToContainTextAsync("USD terms removed.");

        (await TermsOfAsync(walletId)).Should().BeEmpty();
        await Expect(Page.Locator($"#terms-row-{walletId}-USD")).ToHaveCountAsync(0);
        await Expect(offered).ToHaveTextAsync(OfferedToKzt);
    }

    [Fact]
    public async Task Terms_typed_with_a_decimal_point_keep_their_fractions()
    {
        // Review focus 4: the operator's Windows is ru-RU or sr-Latn-RS, where a field parsing with the server's
        // culture refuses "117.35" (comma is the decimal separator there) or reads it as 11735. This only bites on a
        // comma-decimal host, and the E2E suite is not run by CI.
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var name = $"Raiffeisen RSD {Guid.NewGuid():N}";

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/wallets");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        var walletId = await CreateWalletAsync(name, "RSD");

        await Page.SelectOptionAsync($"#terms-new-currency-{walletId}", "EUR");
        await Page.FillAsync($"#terms-new-rate-{walletId}", "117.35");
        await Page.FillAsync($"#terms-new-fee-percent-{walletId}", "1.5");
        await Page.ClickAsync($"#terms-add-{walletId}");
        await Expect(Page.Locator("#wallets-saved")).ToContainTextAsync("EUR terms added.");

        (await TermsOfAsync(walletId)).Should().Equal(
            [new WalletTermsDetails(CurrencyCode.Eur, 117.35m, 1.5m, null, null)],
            "the terms fields are culture-pinned to invariant, so a decimal point parses the same on any server");
        await Expect(Page.Locator($"#terms-rate-{walletId}-EUR")).ToHaveValueAsync("117.35");
        await Expect(Page.Locator($"#terms-fee-percent-{walletId}-EUR")).ToHaveValueAsync("1.5");
    }

    [Fact]
    public async Task Terms_without_a_rate_are_refused_inline()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var name = $"Wise EUR {Guid.NewGuid():N}";

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/wallets");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        var walletId = await CreateWalletAsync(name, "EUR");

        await Page.SelectOptionAsync($"#terms-new-currency-{walletId}", "RSD");
        await Page.FillAsync($"#terms-new-fee-percent-{walletId}", "1");
        await Page.ClickAsync($"#terms-add-{walletId}");

        await Expect(Page.Locator("#wallets-error")).ToContainTextAsync("Enter a rate above zero for RSD.");
        await Expect(Page.Locator("#wallets-saved")).Not.ToBeVisibleAsync();
        (await TermsOfAsync(walletId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Two_cash_wallets_in_two_currencies_are_each_the_cash_default_for_their_own()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var cancellationToken = TestContext.Current.CancellationToken;
        var dinarName = $"Cash RSD {Guid.NewGuid():N}";
        var euroName = $"Cash EUR {Guid.NewGuid():N}";

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/wallets");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        var dinarId = await CreateWalletAsync(dinarName, "RSD");
        var euroId = await CreateWalletAsync(euroName, "EUR");

        await Expect(Page.Locator($"#wallet-{dinarId}")).ToContainTextAsync("Default RSD wallet for");
        await Expect(Page.Locator($"#wallet-{euroId}")).ToContainTextAsync(
            "Card and cash defaults are per currency: this one applies to EUR only.");

        await Page.SelectOptionAsync($"#wallet-payment-default-{dinarId}", "Cash");
        (await WaitForPaymentDefaultAsync(dinarId, WalletPaymentDefault.Cash, cancellationToken))
            .Should().Be(WalletPaymentDefault.Cash);

        await Page.SelectOptionAsync($"#wallet-payment-default-{euroId}", "Cash");
        (await WaitForPaymentDefaultAsync(euroId, WalletPaymentDefault.Cash, cancellationToken))
            .Should().Be(WalletPaymentDefault.Cash);

        // SetPaymentDefaultAsync clears and sets in one database transaction: once EUR's default reads back, a clear of
        // the RSD wallet's would already be committed.
        await using var db = OpenDb();
        (await db.Wallets.AsNoTracking().SingleAsync(w => w.Id == dinarId, cancellationToken)).DefaultForPayment
            .Should().Be(WalletPaymentDefault.Cash, "each currency keeps its own cash default (T-13)");
        await Expect(Page.Locator($"#wallet-payment-default-{dinarId}")).ToHaveValueAsync("Cash");
    }

    [Fact]
    public async Task Terms_typed_with_a_decimal_comma_keep_their_fractions_and_an_unreadable_figure_is_refused()
    {
        // Amendment 27: the operator types a decimal comma, and MudBlazor's own converter reads "117,35" as 11735 under
        // the invariant culture and an unreadable "1.234,5" as no fee at all. This bites on any host - but the E2E
        // suite is not run by CI; DecimalFieldConverterTests is the guard CI runs.
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var name = $"Raiffeisen RSD {Guid.NewGuid():N}";

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/wallets");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        var walletId = await CreateWalletAsync(name, "RSD");

        await Page.SelectOptionAsync($"#terms-new-currency-{walletId}", "EUR");
        await Page.FillAsync($"#terms-new-rate-{walletId}", "117,35");
        await Page.FillAsync($"#terms-new-fee-percent-{walletId}", "1.234,5");
        await Page.ClickAsync($"#terms-add-{walletId}");

        await Expect(Page.Locator("#wallets-error")).ToContainTextAsync("The EUR terms have a field that is not a number.");
        (await TermsOfAsync(walletId)).Should().BeEmpty("an unreadable fee must never be saved as no fee");

        // Once the refused field loses focus, MudBlazor empties it but keeps its error: the operator sees an empty fee
        // marked in red, and the alert says how to get out of it.
        await Expect(Page.Locator($"#terms-new-fee-percent-{walletId}")).ToHaveValueAsync("");
        await Expect(FieldError($"terms-new-fee-percent-{walletId}")).ToContainTextAsync("Not a number");
        await Expect(Page.Locator("#wallets-error")).ToContainTextAsync("Type the figure again in the field marked in red.");

        // Clicked again, the empty-looking fee still refuses the row. Had this Add stored the fee as none, EUR would be
        // a saved row by now and the Add below could not report EUR terms added.
        await Page.ClickAsync($"#terms-add-{walletId}");
        await Expect(Page.Locator("#wallets-error")).ToContainTextAsync("The EUR terms have a field that is not a number.");

        await Page.FillAsync($"#terms-new-fee-percent-{walletId}", "1,5");
        await Page.ClickAsync($"#terms-add-{walletId}");
        await Expect(Page.Locator("#wallets-saved")).ToContainTextAsync("EUR terms added.");

        (await TermsOfAsync(walletId)).Should().Equal(
            [new WalletTermsDetails(CurrencyCode.Eur, 117.35m, 1.5m, null, null)],
            "a comma is read as the decimal separator, never as a thousands separator");
        await Expect(Page.Locator($"#terms-rate-{walletId}-EUR")).ToHaveValueAsync("117.35");
        await Expect(Page.Locator($"#terms-fee-percent-{walletId}-EUR")).ToHaveValueAsync("1.5");
    }

    [Fact]
    public async Task A_refused_terms_figure_loses_its_error_when_the_page_reloads()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var name = $"Kaspi KZT {Guid.NewGuid():N}";

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/wallets");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        var walletId = await CreateWalletAsync(name, "KZT");

        await Page.SelectOptionAsync($"#terms-new-currency-{walletId}", "USD");
        await Page.FillAsync($"#terms-new-rate-{walletId}", "520");
        await Page.FillAsync($"#terms-new-fee-percent-{walletId}", "1");
        await Page.ClickAsync($"#terms-add-{walletId}");
        await Expect(Page.Locator("#wallets-saved")).ToContainTextAsync("USD terms added.");

        await Page.FillAsync($"#terms-fee-percent-{walletId}-USD", "1.234,5");
        await Page.ClickAsync($"#terms-save-{walletId}-USD");
        await Expect(Page.Locator("#wallets-error")).ToContainTextAsync("The USD terms have a field that is not a number.");
        await Expect(FieldError($"terms-fee-percent-{walletId}-USD")).ToContainTextAsync("Not a number");

        // Any other action reloads the rows with fresh converters; the field's error must go with the one that raised
        // it, not stay red under the stored figure the field shows again.
        await Page.ClickAsync($"#save-aliases-{walletId}");
        await Expect(Page.Locator("#wallets-saved")).ToContainTextAsync("Aliases saved.");
        await Expect(Page.Locator($"#terms-fee-percent-{walletId}-USD")).ToHaveValueAsync("1");
        await Expect(FieldError($"terms-fee-percent-{walletId}-USD")).ToHaveCountAsync(0);

        (await TermsOfAsync(walletId)).Should().Equal(new WalletTermsDetails(CurrencyCode.Usd, 520m, 1m, null, null));
    }

    async Task<Guid> CreateWalletAsync(string name, string currency)
    {
        await Page.FillAsync("#new-wallet-name", name);
        await Page.SelectOptionAsync("#new-wallet-currency", currency);
        await Page.FillAsync("#new-wallet-opening", "0");
        await Page.FillAsync("#new-wallet-date", "2026-09-01");
        await Page.ClickAsync("#create-wallet");
        await Expect(Page.Locator("#wallets-saved")).ToContainTextAsync($"Created {name}.");
        return await WaitForWalletIdAsync(name, TestContext.Current.CancellationToken);
    }

    // Each terms confirmation names its own action and currency, so once it shows, that action has committed - unlike
    // the repeated "Payment default saved." that WaitForPaymentDefaultAsync has to poll around.
    async Task<IReadOnlyList<WalletTermsDetails>> TermsOfAsync(Guid walletId)
    {
        await using var db = OpenDb();
        return await new EfWalletFxTerms(db).ListAsync(walletId, TestContext.Current.CancellationToken);
    }

    // MudBlazor writes a field's conversion error under it, outside the <input> the id names.
    ILocator FieldError(string inputId) =>
        Page.Locator(".noof-field", new() { Has = Page.Locator($"#{inputId}") })
            .Locator(".mud-input-helper-text.mud-input-error");

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
