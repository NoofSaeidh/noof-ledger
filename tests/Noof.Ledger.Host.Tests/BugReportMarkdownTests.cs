using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Diagnostics.BugReports;
using Noof.Ledger.Application.Diagnostics.Integrity;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;
using Noof.Ledger.TestKit;

namespace Noof.Ledger.Host.Tests;

// IBugReportMarkdown as AddNoofApplication registers it, over the real FindingText: the findings sections are
// FindingsBlock's text, aged from the snapshot for "then" and from the export for "now".
public class BugReportMarkdownTests
{
    static readonly DateTimeOffset GeneratedAt = new(2026, 10, 2, 14, 5, 0, TimeSpan.Zero);
    static readonly Guid RecordA = new("7a1c0000-0000-4000-8000-000000000003");
    static readonly Guid RecordC = new("7a1c0000-0000-4000-8000-000000000012");
    static readonly Guid FailedJobId = new("5f0c0000-0000-4000-8000-000000000001");

    static ServiceProvider Services() => new ServiceCollection()
        .AddNoofApplication(
            new SlowOperationOptions(),
            new FiscalVerificationUrlOptions { VerificationUrlPrefix = "https://suf.purs.gov.rs/v/?vl=" })
        .BuildServiceProvider();

    static string Render(params BugReportDocument[] reports)
    {
        using var services = Services();
        return services.GetRequiredService<IBugReportMarkdown>().Render(reports, GeneratedAt);
    }

    static IntegrityFinding WaitingCorrection(string lastError) => new(
        IntegrityCheck.NotApplied, IntegrityGroup.WaitingOnYou, RecordA, WalletId: null, FailedJobId,
        [
            new TextFact("Waiting for", "A correction that never applied"),
            new TextFact("Job", "Correct"),
            new TextFact("Last error", lastError),
            new SinceFact("Idle for", new DateTimeOffset(2026, 9, 29, 7, 0, 0, TimeSpan.Zero)),
            new DateFact("Date", new DateOnly(2026, 9, 28)),
            new MoneyFact("Amount", 250m, CurrencyCode.Rsd),
        ]);

    static BugReportDocument LinkedFromTelegram() => new(
        Number: 3,
        CreatedAt: new DateTimeOffset(2026, 9, 30, 8, 15, 0, TimeSpan.Zero),
        Source: BugReportSource.Telegram,
        Status: BugReportStatus.Open,
        ClosedAt: null,
        Text: "the amount is wrong ``` I paid 2500",
        TransactionId: RecordA,
        SnapshotAt: new DateTimeOffset(2026, 9, 30, 9, 20, 0, TimeSpan.Zero),
        RecordSummary: "Record: Expense · Completed · captured from Text\nDate: 2026-09-28\nWallet: Cash RSD\n"
            + "Text: кофе 250\nLines:\n- кофе · 250.00 RSD · Coffee\nRevisions:\n"
            + "- 2026-09-28 18:00 UTC · Initial · Captured → Completed",
        Revisions:
        [
            new RevisionView(new DateTimeOffset(2026, 9, 28, 18, 0, 0, TimeSpan.Zero), "Initial", "Captured → Completed"),
            new RevisionView(new DateTimeOffset(2026, 9, 29, 6, 0, 0, TimeSpan.Zero), "Correction", "it was 2500"),
        ],
        FindingsThen: [WaitingCorrection("The model call failed.")],
        FindingsNow: [WaitingCorrection("The model call failed.")],
        CollectionFailures: null,
        ExplanationState: BugExplanationState.Done,
        Explanation: "The correction never applied: the model call failed.\nReply to the echo with the amount again.",
        LooksLikeBug: false,
        LogLines:
        [
            new BugReportLogLine(
                new DateTimeOffset(2026, 9, 30, 8, 14, 59, TimeSpan.Zero), LogSeverity.Warning,
                "Noof.Ledger.Host.Workers.CategorizationWorker", "Correction failed",
                "System.InvalidOperationException: The model call failed.\n"
                    + "   at Noof.Ledger.Host.Workers.CategorizationWorker.RunAsync()",
                """{"Stage":"StageFailed"}"""),
            new BugReportLogLine(
                new DateTimeOffset(2026, 9, 30, 8, 14, 58, TimeSpan.Zero), LogSeverity.Debug,
                Source: null, "Claimed job", Exception: null, PropertiesJson: null),
        ]);

    static BugReportDocument Unlinked(int number = 7, string? text = null) => new(
        Number: number,
        CreatedAt: new DateTimeOffset(2026, 10, 1, 20, 40, 0, TimeSpan.Zero),
        Source: BugReportSource.Telegram,
        Status: BugReportStatus.Closed,
        ClosedAt: new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero),
        Text: text,
        TransactionId: null,
        SnapshotAt: null,
        RecordSummary: null,
        Revisions: [],
        FindingsThen: null,
        FindingsNow: null,
        CollectionFailures: null,
        ExplanationState: BugExplanationState.Failed,
        Explanation: null,
        LooksLikeBug: null,
        LogLines: null);

    static BugReportDocument FromDashboard() => new(
        Number: 12,
        CreatedAt: new DateTimeOffset(2026, 10, 2, 11, 0, 0, TimeSpan.Zero),
        Source: BugReportSource.Dashboard,
        Status: BugReportStatus.Open,
        ClosedAt: null,
        Text: null,
        TransactionId: RecordC,
        SnapshotAt: new DateTimeOffset(2026, 10, 2, 11, 0, 0, TimeSpan.Zero),
        RecordSummary: null,
        Revisions: [],
        FindingsThen: [],
        FindingsNow: null,
        CollectionFailures: "record summary: query failed (TimeoutException)",
        ExplanationState: BugExplanationState.Done,
        Explanation: "The entries were written wrong — this is a bug, file it.",
        LooksLikeBug: true,
        LogLines: []);

    static readonly string ThreeReports = """
        # noof-ledger bug reports

        Exported 2026-10-02 14:05 UTC · 3 reports

        > Contains the operator's own financial data. Never paste it into a public issue, commit, PR or backlog entry.

        ## Bug report #3

        - Filed: 2026-09-30 08:15 UTC
        - Source: Telegram
        - Status: Open
        - Record: 7a1c0000-0000-4000-8000-000000000003
        - Snapshot: 2026-09-30 09:20 UTC

        ### Operator's text

        ````text
        the amount is wrong ``` I paid 2500
        ````

        ### Record as filed

        ```text
        Record: Expense · Completed · captured from Text
        Date: 2026-09-28
        Wallet: Cash RSD
        Text: кофе 250
        Lines:
        - кофе · 250.00 RSD · Coffee
        Revisions:
        - 2026-09-28 18:00 UTC · Initial · Captured → Completed
        ```

        ### Revision history

        ```text
        2026-09-28 18:00 UTC · Initial · Captured → Completed
        2026-09-29 06:00 UTC · Correction · it was 2500
        ```

        ### Findings when filed

        ```text
        1. Not applied (Waiting on you)
           Waiting for: A correction that never applied
           Job: Correct
           Last error: The model call failed.
           Idle for: 1 d 2 h (since 2026-09-29 07:00 UTC)
           Date: 2026-09-28
           Amount: 250.00 RSD
        ```

        ### Findings now

        ```text
        1. Not applied (Waiting on you)
           Waiting for: A correction that never applied
           Job: Correct
           Last error: The model call failed.
           Idle for: 3 d 7 h (since 2026-09-29 07:00 UTC)
           Date: 2026-09-28
           Amount: 250.00 RSD
        ```

        ### Not collected

        (nothing)

        ### Explanation

        - State: Done
        - Looks like a bug: no

        ```text
        The correction never applied: the model call failed.
        Reply to the echo with the amount again.
        ```

        ### Log lines (newest first)

        ```text
        2026-09-30 08:14:59 UTC Warning Noof.Ledger.Host.Workers.CategorizationWorker
          Correction failed
          properties: {"Stage":"StageFailed"}
          exception: System.InvalidOperationException: The model call failed.
             at Noof.Ledger.Host.Workers.CategorizationWorker.RunAsync()
        2026-09-30 08:14:58 UTC Debug -
          Claimed job
        ```

        ## Bug report #7

        - Filed: 2026-10-01 20:40 UTC
        - Source: Telegram
        - Status: Closed (closed 2026-10-02 09:00 UTC)
        - Record: none
        - Snapshot: not taken yet

        ### Operator's text

        (none)

        ### Record as filed

        (none)

        ### Revision history

        (none)

        ### Findings when filed

        (not collected)

        ### Findings now

        (no record)

        ### Not collected

        (nothing)

        ### Explanation

        - State: Failed
        - Looks like a bug: —

        (none)

        ### Log lines (newest first)

        (not collected)

        ## Bug report #12

        - Filed: 2026-10-02 11:00 UTC
        - Source: Dashboard
        - Status: Open
        - Record: 7a1c0000-0000-4000-8000-000000000012
        - Snapshot: 2026-10-02 11:00 UTC

        ### Operator's text

        (none)

        ### Record as filed

        (none)

        ### Revision history

        (none)

        ### Findings when filed

        (none)

        ### Findings now

        (not available)

        ### Not collected

        ```text
        record summary: query failed (TimeoutException)
        ```

        ### Explanation

        - State: Done
        - Looks like a bug: yes

        ```text
        The entries were written wrong — this is a bug, file it.
        ```

        ### Log lines (newest first)

        (none)
        """.ReplaceLineEndings("\n") + "\n";

    // fi-FI writes "08.15" for a time and "250,00" for an amount: every figure here must still read invariant.
    [Fact]
    public void Reports_render_in_number_order_with_every_section_of_the_template()
    {
        using var culture = new CultureScope("fi-FI");

        Render(FromDashboard(), Unlinked(), LinkedFromTelegram()).Should().Be(ThreeReports);
    }

    [Fact]
    public void One_report_is_counted_in_the_singular()
    {
        Render(Unlinked()).Should().Contain("\nExported 2026-10-02 14:05 UTC · 1 report\n");
    }

    [Theory]
    [InlineData("plain words", "```")]
    [InlineData("one ` tick and `` two", "```")]
    [InlineData("a ``` run", "````")]
    [InlineData("````` five", "``````")]
    public void The_fence_is_one_backtick_longer_than_the_longest_run_inside_and_never_shorter_than_three(
        string text, string fence)
    {
        Render(Unlinked(text: text))
            .Should().Contain($"### Operator's text\n\n{fence}text\n{text}\n{fence}\n\n### Record as filed");
    }

    [Fact]
    public void Windows_line_endings_inside_a_field_become_the_documents_own()
    {
        Render(Unlinked(text: "first line\r\nsecond line"))
            .Should().Contain("```text\nfirst line\nsecond line\n```").And.NotContain("\r");
    }

    [Fact]
    public void AddNoofApplication_registers_one_renderer_for_the_whole_process()
    {
        using var services = Services();
        using var first = services.CreateScope();
        using var second = services.CreateScope();

        first.ServiceProvider.GetRequiredService<IBugReportMarkdown>()
            .Should().BeSameAs(second.ServiceProvider.GetRequiredService<IBugReportMarkdown>());
    }

    const string FiscalLink = "https://suf.purs.gov.rs/v/?vl=QUJDREVGR0hJSktMTU5PUFFSU1RVVldY";

    static BugReportLogLine HostileLogLine() => new(
        new DateTimeOffset(2026, 10, 2, 9, 59, 0, TimeSpan.Zero), LogSeverity.Error,
        "Noof.Ledger.Host.Workers.CategorizationWorker",
        "Fetch failed ```` <script>alert(1)</script>\n## Bug report #99 " + FiscalLink,
        "System.Exception: " + FiscalLink, "{\"Url\":\"" + FiscalLink + "\"}");

    // An operator text, a wallet name, a line description and a log message that each try to leave their fence or
    // carry a fiscal link.
    static BugReportDocument Hostile() => new(
        Number: 5,
        CreatedAt: new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero),
        Source: BugReportSource.Telegram,
        Status: BugReportStatus.Open,
        ClosedAt: null,
        Text: "the total ```` and ~~~\n## Bug report #99\n<script>alert(1)</script> " + FiscalLink,
        TransactionId: RecordA,
        SnapshotAt: new DateTimeOffset(2026, 10, 2, 10, 1, 0, TimeSpan.Zero),
        RecordSummary: "Record: Expense · Completed · captured from Text\nWallet: Cash ````RSD\nText: кофе "
            + FiscalLink + " 250\nLines:\n- кофе ~~~ <script> · 250.00 RSD · Coffee",
        Revisions: [new RevisionView(new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.Zero), "Edit", "кофе 250 " + FiscalLink)],
        FindingsThen: [WaitingCorrection("fetch failed: " + FiscalLink)],
        FindingsNow: null,
        CollectionFailures: null,
        ExplanationState: BugExplanationState.Done,
        Explanation: "See " + FiscalLink + " — the receipt was read twice.",
        LooksLikeBug: false,
        LogLines: [HostileLogLine()]);

    static readonly string HostileReport = """
        # noof-ledger bug reports

        Exported 2026-10-02 14:05 UTC · 1 report

        > Contains the operator's own financial data. Never paste it into a public issue, commit, PR or backlog entry.

        ## Bug report #5

        - Filed: 2026-10-02 10:00 UTC
        - Source: Telegram
        - Status: Open
        - Record: 7a1c0000-0000-4000-8000-000000000003
        - Snapshot: 2026-10-02 10:01 UTC

        ### Operator's text

        `````text
        the total ```` and ~~~
        ## Bug report #99
        <script>alert(1)</script>
        `````

        ### Record as filed

        `````text
        Record: Expense · Completed · captured from Text
        Wallet: Cash ````RSD
        Text: кофе 250
        Lines:
        - кофе ~~~ <script> · 250.00 RSD · Coffee
        `````

        ### Revision history

        ```text
        2026-10-01 18:00 UTC · Edit · кофе 250
        ```

        ### Findings when filed

        ```text
        1. Not applied (Waiting on you)
           Waiting for: A correction that never applied
           Job: Correct
           Last error: fetch failed:
           Idle for: 3 d 3 h (since 2026-09-29 07:00 UTC)
           Date: 2026-09-28
           Amount: 250.00 RSD
        ```

        ### Findings now

        (not available)

        ### Not collected

        (nothing)

        ### Explanation

        - State: Done
        - Looks like a bug: no

        ```text
        See — the receipt was read twice.
        ```

        ### Log lines (newest first)

        `````text
        2026-10-02 09:59:00 UTC Error Noof.Ledger.Host.Workers.CategorizationWorker
          Fetch failed ```` <script>alert(1)</script>
          ## Bug report #99
          properties: {"Url":"
          exception: System.Exception:
        `````
        """.ReplaceLineEndings("\n") + "\n";

    [Fact]
    public void Hostile_free_text_stays_inside_its_fence_and_loses_its_fiscal_link()
    {
        Render(Hostile()).Should().Be(HostileReport);
    }

    [Fact]
    public void No_fiscal_link_reaches_the_document_from_any_field()
    {
        var markdown = Render(Hostile() with
        {
            FindingsNow = [WaitingCorrection("fetch failed: " + FiscalLink)],
            CollectionFailures = "log lines: query failed (PostgresException) " + FiscalLink,
            LogLines = [HostileLogLine() with { Source = "Noof.Ledger.Host.Workers.CategorizationWorker " + FiscalLink }],
        });

        markdown.Should().NotContain("suf.purs.gov.rs");
        markdown.Should().Contain("```text\nlog lines: query failed (PostgresException)\n```");
        markdown.Split("   Last error: fetch failed:\n").Should().HaveCount(3,
            "the finding renders twice, then and now, each without its link and each still on its own line");
    }

    // A link runs to the next whitespace, so it can carry backticks away with it; removing one mid-text leaves a space,
    // so two runs it separated never merge. Either way the fence fits the text as rendered, not as stored.
    [Theory]
    [InlineData("see " + FiscalLink + "```````` here", "see here", "```")]
    [InlineData("``` " + FiscalLink + " ```", "``` ```", "````")]
    public void The_fence_is_measured_on_the_text_left_once_its_fiscal_link_is_gone(
        string text, string rendered, string fence)
    {
        Render(Unlinked(text: text))
            .Should().Contain($"### Operator's text\n\n{fence}text\n{rendered}\n{fence}\n\n### Record as filed");
    }
}
