using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Persistence.Diagnostics;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfLogQueryTests(PostgresFixture fixture)
{
    static readonly Guid TransactionA = new("00000000-0000-0000-0002-000000000001");
    static readonly Guid TransactionB = new("00000000-0000-0000-0002-000000000002");

    static AppLogEntry Row(int n, LogSeverity level, DateTimeOffset loggedAt, string message,
        string? source = "Noof.Ledger.Telegram.TelegramPollingService", Guid? transactionId = null) => new()
    {
        Id = n,
        LoggedAt = loggedAt,
        Level = level,
        Source = source,
        Message = message,
        Template = message,
        Exception = null,
        TransactionId = transactionId,
        PropertiesJson = """{"SourceContext":"Noof.Ledger.Telegram.TelegramPollingService"}""",
    };

    static async Task SeedAsync(LedgerDbContext db, params AppLogEntry[] rows)
    {
        db.AppLogs.AddRange(rows);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Rows_below_the_minimum_level_are_excluded()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await SeedAsync(db,
            Row(1, LogSeverity.Debug, DateTimeOffset.Parse("2026-09-25T10:00:00Z"), "debug line"),
            Row(2, LogSeverity.Warning, DateTimeOffset.Parse("2026-09-25T10:01:00Z"), "warning line"));

        var page = await new EfLogQuery(db).QueryAsync(
            new LogFilter(MinLevel: LogSeverity.Information), pageIndex: 0, pageSize: 50, TestContext.Current.CancellationToken);

        page.Rows.Should().ContainSingle().Which.Message.Should().Be("warning line");
        page.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task From_and_to_bound_the_time_range_inclusively()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await SeedAsync(db,
            Row(1, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T09:59:59Z"), "too early"),
            Row(2, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T10:00:00Z"), "at from"),
            Row(3, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T11:00:00Z"), "at to"),
            Row(4, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T11:00:01Z"), "too late"));

        var page = await new EfLogQuery(db).QueryAsync(
            new LogFilter(From: DateTimeOffset.Parse("2026-09-25T10:00:00Z"), To: DateTimeOffset.Parse("2026-09-25T11:00:00Z")),
            pageIndex: 0, pageSize: 50, TestContext.Current.CancellationToken);

        page.Rows.Select(r => r.Message).Should().BeEquivalentTo(["at from", "at to"]);
    }

    [Fact]
    public async Task Text_filters_by_ILIKE_on_message_and_escapes_wildcards_literally()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await SeedAsync(db,
            Row(1, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T10:00:00Z"), "polling FAILED for chat 100%"),
            Row(2, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T10:01:00Z"), "polling succeeded"));

        var page = await new EfLogQuery(db).QueryAsync(
            new LogFilter(Text: "100%"), pageIndex: 0, pageSize: 50, TestContext.Current.CancellationToken);

        page.Rows.Should().ContainSingle().Which.Message.Should().Contain("100%");
    }

    [Fact]
    public async Task Source_filters_by_prefix_so_a_namespace_narrows_to_every_logger_under_it()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await SeedAsync(db,
            Row(1, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T10:00:00Z"), "a", source: "Noof.Ledger.Ai.Anthropic.AnthropicChatClient"),
            Row(2, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T10:01:00Z"), "b", source: "Noof.Ledger.Ai.Groq.GroqTranscriber"),
            Row(3, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T10:02:00Z"), "c", source: "Noof.Ledger.Telegram.TelegramPollingService"));

        var page = await new EfLogQuery(db).QueryAsync(
            new LogFilter(Source: "Noof.Ledger.Ai"), pageIndex: 0, pageSize: 50, TestContext.Current.CancellationToken);

        page.Rows.Select(r => r.Message).Should().BeEquivalentTo(["a", "b"]);
    }

    [Fact]
    public async Task Source_filtering_escapes_ILIKE_wildcards_in_the_prefix_literally()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await SeedAsync(db,
            Row(1, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T10:00:00Z"), "a", source: "Noof%Ledger.Weird"),
            Row(2, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T10:01:00Z"), "b", source: "NoofXLedger.Weird"));

        var page = await new EfLogQuery(db).QueryAsync(
            new LogFilter(Source: "Noof%Ledger"), pageIndex: 0, pageSize: 50, TestContext.Current.CancellationToken);

        page.Rows.Should().ContainSingle().Which.Message.Should().Be("a");
    }

    [Fact]
    public async Task TransactionId_filters_exactly()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await SeedAsync(db,
            Row(1, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T10:00:00Z"), "a", transactionId: TransactionA),
            Row(2, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T10:01:00Z"), "b", transactionId: TransactionB));

        var page = await new EfLogQuery(db).QueryAsync(
            new LogFilter(TransactionId: TransactionA), pageIndex: 0, pageSize: 50, TestContext.Current.CancellationToken);

        page.Rows.Should().ContainSingle().Which.TransactionId.Should().Be(TransactionA);
    }

    [Fact]
    public async Task Rows_are_newest_first_by_logged_at_then_id_and_TotalCount_ignores_paging()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var same = DateTimeOffset.Parse("2026-09-25T10:00:00Z");
        await SeedAsync(db,
            Row(1, LogSeverity.Information, same, "first"),
            Row(2, LogSeverity.Information, same, "second"),
            Row(3, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T10:01:00Z"), "third"));

        var page = await new EfLogQuery(db).QueryAsync(
            new LogFilter(), pageIndex: 0, pageSize: 2, TestContext.Current.CancellationToken);

        page.Rows.Select(r => r.Message).Should().Equal("third", "second");
        page.TotalCount.Should().Be(3);
    }

    [Fact]
    public async Task OldestFirst_reverses_the_order_and_ties_break_by_id_ascending()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var same = DateTimeOffset.Parse("2026-09-25T10:00:00Z");
        await SeedAsync(db,
            Row(1, LogSeverity.Information, same, "first"),
            Row(2, LogSeverity.Information, same, "second"),
            Row(3, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T10:01:00Z"), "third"));

        var page = await new EfLogQuery(db).QueryAsync(
            new LogFilter(Sort: LogSortOrder.OldestFirst), pageIndex: 0, pageSize: 50, TestContext.Current.CancellationToken);

        page.Rows.Select(r => r.Message).Should().Equal("first", "second", "third");
    }

    [Fact]
    public async Task PageIndex_skips_the_prior_pages()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await SeedAsync(db,
            Row(1, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T10:00:00Z"), "oldest"),
            Row(2, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T10:01:00Z"), "middle"),
            Row(3, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T10:02:00Z"), "newest"));

        var page = await new EfLogQuery(db).QueryAsync(
            new LogFilter(), pageIndex: 1, pageSize: 1, TestContext.Current.CancellationToken);

        page.Rows.Should().ContainSingle().Which.Message.Should().Be("middle");
        page.TotalCount.Should().Be(3);
    }
}
