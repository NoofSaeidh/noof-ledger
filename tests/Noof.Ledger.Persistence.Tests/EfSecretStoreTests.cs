using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Persistence.Secrets;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfSecretStoreTests(PostgresFixture fixture) : IDisposable
{
    readonly DirectoryInfo keyRing = Directory.CreateTempSubdirectory("noof-secret-test-");

    public void Dispose() => keyRing.Delete(recursive: true);

    EfSecretStore CreateStore(LedgerDbContext db, TimeProvider? timeProvider = null) =>
        new(db, DataProtectionProvider.Create(keyRing), timeProvider ?? TimeProvider.System);

    [Fact]
    public async Task An_unknown_key_returns_Missing()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = CreateStore(db);

        var result = await store.GetAsync("does-not-exist", TestContext.Current.CancellationToken);

        result.Should().Be(new SecretResult(SecretState.Missing, null));
    }

    [Fact]
    public async Task Setting_then_getting_round_trips_the_plaintext()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = CreateStore(db);

        await store.SetAsync(SecretKeys.AnthropicApiKey, "sk-ant-secret", TestContext.Current.CancellationToken);
        var result = await store.GetAsync(SecretKeys.AnthropicApiKey, TestContext.Current.CancellationToken);

        result.Should().Be(new SecretResult(SecretState.Present, "sk-ant-secret"));
    }

    [Fact]
    public async Task Setting_the_same_key_twice_updates_the_row_instead_of_inserting_a_second_one()
    {
        // Mirrors commit a828608: EfUserStore once upserted by Id instead of the natural key, so a
        // second write silently inserted instead of updating — on the only account-recovery path
        // there was. AppSecret's primary key IS the lookup key, so there is no separate surrogate-key
        // bug possible here, but this still catches the simpler failure of a SetAsync that always inserts.
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = CreateStore(db);

        await store.SetAsync(SecretKeys.AnthropicApiKey, "first-value", TestContext.Current.CancellationToken);
        await store.SetAsync(SecretKeys.AnthropicApiKey, "second-value", TestContext.Current.CancellationToken);

        var result = await store.GetAsync(SecretKeys.AnthropicApiKey, TestContext.Current.CancellationToken);
        result.Should().Be(new SecretResult(SecretState.Present, "second-value"));

        var rowCount = await db.Secrets.CountAsync(TestContext.Current.CancellationToken);
        rowCount.Should().Be(1, "a second SetAsync for the same key must update, not insert");
    }

    [Fact]
    public async Task Setting_the_same_key_twice_advances_updated_at_using_the_injected_clock()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var store = CreateStore(db, clock);

        await store.SetAsync(SecretKeys.AnthropicApiKey, "first", TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromHours(1));
        await store.SetAsync(SecretKeys.AnthropicApiKey, "second", TestContext.Current.CancellationToken);

        var row = await db.Secrets.SingleAsync(TestContext.Current.CancellationToken);
        row.UpdatedAt.Should().Be(DateTimeOffset.Parse("2026-01-01T01:00:00Z"),
            "UpdatedAt must come from the injected TimeProvider, never DateTimeOffset.UtcNow directly");
    }

    [Fact]
    public async Task A_hand_edited_ciphertext_column_returns_Unreadable_instead_of_throwing()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = CreateStore(db);

        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO app_secret (key, ciphertext, updated_at) VALUES ({SecretKeys.TelegramBotToken}, 'not-a-real-ciphertext', now())",
            TestContext.Current.CancellationToken);

        var result = await store.GetAsync(SecretKeys.TelegramBotToken, TestContext.Current.CancellationToken);

        result.Should().Be(new SecretResult(SecretState.Unreadable, null));
    }

    [Fact]
    public async Task Status_reports_missing_for_a_key_that_was_never_set()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = CreateStore(db);

        var status = await store.GetStatusAsync("never-set", TestContext.Current.CancellationToken);

        status.State.Should().Be(SecretState.Missing);
        status.UpdatedAt.Should().BeNull();
    }

    [Fact]
    public async Task Status_reports_present_with_the_time_it_was_set()
    {
        var clock = new FakeTimeProvider();
        clock.SetUtcNow(new DateTimeOffset(2026, 9, 21, 10, 0, 0, TimeSpan.Zero));

        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = CreateStore(db, clock);

        await store.SetAsync(SecretKeys.AnthropicApiKey, "sk-whatever", TestContext.Current.CancellationToken);

        var status = await store.GetStatusAsync(SecretKeys.AnthropicApiKey, TestContext.Current.CancellationToken);

        status.State.Should().Be(SecretState.Present);
        status.UpdatedAt.Should().Be(clock.GetUtcNow());
    }

    [Fact]
    public async Task Status_reports_unreadable_when_the_ciphertext_will_not_decrypt()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = CreateStore(db);

        await store.SetAsync(SecretKeys.TelegramBotToken, "123456:real-looking-token", TestContext.Current.CancellationToken);

        await db.Database.ExecuteSqlAsync(
            $"UPDATE app_secret SET ciphertext = 'not-protected-text' WHERE key = {SecretKeys.TelegramBotToken}",
            TestContext.Current.CancellationToken);

        // Without this, FindAsync below resolves the row from this context's identity map — the
        // entity tracked in memory since SetAsync — instead of querying the database, so the raw SQL
        // corruption above would be invisible to it. A real corrupted key ring is only ever
        // discovered from a fresh, unrelated request scope (LedgerDbContext is scoped per request),
        // never from the same context that just wrote the correct ciphertext moments earlier, so
        // clearing the tracker here reproduces that, rather than a same-context artifact this test
        // would otherwise introduce.
        db.ChangeTracker.Clear();

        var status = await store.GetStatusAsync(SecretKeys.TelegramBotToken, TestContext.Current.CancellationToken);

        status.State.Should().Be(SecretState.Unreadable);
    }
}
