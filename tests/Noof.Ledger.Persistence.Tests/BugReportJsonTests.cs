using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Diagnostics.Integrity;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.BugReports;

namespace Noof.Ledger.Persistence.Tests;

public class BugReportJsonTests
{
    const string FiscalLink = "https://suf.purs.gov.rs/v/?vl=QUJDREVGR0hJSktMTU5PUFFSU1RVVldY";
    static readonly Guid RecordId = new("7a1c0000-0000-4000-8000-000000000900");
    static readonly DateTimeOffset LoggedAt = new DateTimeOffset(2026, 10, 2, 13, 58, 1, TimeSpan.Zero).AddTicks(1_234_560);
    static readonly IFiscalVerificationUrl VerificationUrl =
        new FiscalVerificationUrl(new FiscalVerificationUrlOptions { VerificationUrlPrefix = "https://suf.purs.gov.rs/v/?vl=" });

    static IntegrityFinding EveryKind(string wallet = "Cash RSD") => new(
        IntegrityCheck.PostingsDisagree, IntegrityGroup.Bug, RecordId, WalletId: null, JobId: null,
        [
            new TextFact("Wallet", wallet),
            new MoneyFact("Expected", -250.0000m, CurrencyCode.Rsd),
            new DateFact("Date", new DateOnly(2026, 9, 18)),
            new SinceFact("Idle for", new DateTimeOffset(2026, 9, 30, 11, 4, 0, TimeSpan.Zero)),
            new CountFact("Fee lines", 2),
        ]);

    static LogRow Row(string message, string? exception = null, string? properties = null) =>
        new(4711, LoggedAt, LogSeverity.Warning, "Noof.Ledger.Host.Workers.CategorizationWorker", message, exception,
            TransactionId: null, properties);

    static bool SameJson(string actual, string expected) => JsonNode.DeepEquals(JsonNode.Parse(actual), JsonNode.Parse(expected));

    [Fact]
    public void Findings_are_written_in_the_contract_shape()
    {
        var json = BugReportJson.WriteFindings([EveryKind()], VerificationUrl);

        SameJson(json, """
            [{"check":"PostingsDisagree","group":"Bug","transaction_id":"7a1c0000-0000-4000-8000-000000000900",
              "wallet_id":null,"job_id":null,
              "facts":[{"kind":"text","name":"Wallet","text":"Cash RSD"},
                       {"kind":"money","name":"Expected","amount":"-250.0000","currency":"RSD"},
                       {"kind":"date","name":"Date","day":"2026-09-18"},
                       {"kind":"since","name":"Idle for","since":"2026-09-30T11:04:00.0000000+00:00"},
                       {"kind":"count","name":"Fee lines","count":2}]}]
            """).Should().BeTrue(json);
    }

    [Fact]
    public void Findings_read_back_as_they_were_written()
    {
        var finding = EveryKind();

        BugReportJson.ReadFindings(BugReportJson.WriteFindings([finding], VerificationUrl))
            .Should().BeEquivalentTo(new[] { finding }, options => options.PreferringRuntimeMemberTypes().WithStrictOrdering());
    }

    [Fact]
    public void A_text_fact_loses_its_fiscal_link_and_a_text_of_nothing_but_a_link_becomes_empty()
    {
        var json = BugReportJson.WriteFindings([EveryKind($"Cash {FiscalLink} RSD"), EveryKind(FiscalLink)], VerificationUrl);

        json.Should().NotContain("suf.purs.gov.rs");
        BugReportJson.ReadFindings(json)!.Select(finding => ((TextFact)finding.Facts[0]).Text).Should().Equal("Cash RSD", "");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("""[{"check":"Nope","group":"Bug","transaction_id":null,"wallet_id":null,"job_id":null,"facts":[]}]""")]
    [InlineData("""[{"check":"1","group":"Bug","transaction_id":null,"wallet_id":null,"job_id":null,"facts":[]}]""")]
    [InlineData("""[{"check":"Unknown","group":"Bug","transaction_id":null,"wallet_id":null,"job_id":null,"facts":[]}]""")]
    [InlineData("""[{"check":"PostingsDisagree","group":"Unknown","transaction_id":null,"wallet_id":null,"job_id":null,"facts":[]}]""")]
    [InlineData("""[{"check":"PostingsDisagree","group":"Bug","transaction_id":null,"wallet_id":null,"job_id":null}]""")]
    [InlineData("""[{"check":"PostingsDisagree","group":"Bug","transaction_id":null,"wallet_id":null,"job_id":null,"facts":[{"kind":"money","name":"Expected","amount":"-250","currency":"RS1"}]}]""")]
    [InlineData("""[{"check":"PostingsDisagree","group":"Bug","transaction_id":null,"wallet_id":null,"job_id":null,"facts":[{"kind":"weather","name":"Sky"}]}]""")]
    public void A_findings_column_that_cannot_be_read_back_reads_as_not_collected(string json)
    {
        BugReportJson.ReadFindings(json).Should().BeNull();
    }

    [Fact]
    public void An_empty_findings_column_reads_as_not_collected()
    {
        BugReportJson.ReadFindings(null).Should().BeNull();
    }

    [Fact]
    public void Log_lines_are_written_in_the_contract_shape_without_the_row_id()
    {
        var json = BugReportJson.WriteLogLines(
            [Row("Categorized", properties: """{"Stage":"Categorized","Attempt":3,"Scope":{"a":1}}""")], VerificationUrl);

        json.Should().NotContain("4711");
        SameJson(json, """
            [{"logged_at":"2026-10-02T13:58:01.1234560+00:00","level":"Warning",
              "source":"Noof.Ledger.Host.Workers.CategorizationWorker","message":"Categorized","exception":null,
              "properties":{"Stage":"Categorized","Attempt":"3","Scope":"{\"a\":1}"}}]
            """).Should().BeTrue(json);
    }

    [Fact]
    public void Log_lines_read_back_with_their_properties_as_a_json_object()
    {
        var line = BugReportJson.ReadLogLines(BugReportJson.WriteLogLines(
            [Row("Categorized", "System.Exception: boom", """{"Stage":"Categorized","Attempt":3}""")], VerificationUrl))!.Single();

        (line.LoggedAt, line.Level, line.Source, line.Message, line.Exception).Should().Be(
            (LoggedAt, LogSeverity.Warning, "Noof.Ledger.Host.Workers.CategorizationWorker", "Categorized", "System.Exception: boom"));
        SameJson(line.PropertiesJson!, """{"Stage":"Categorized","Attempt":"3"}""").Should().BeTrue(line.PropertiesJson);
    }

    [Fact]
    public void Message_exception_and_property_values_lose_their_fiscal_link_before_they_are_cut_to_2000_characters()
    {
        var properties = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["Url"] = FiscalLink,
            ["Note"] = new string('y', 2100),
        });
        var lines = BugReportJson.ReadLogLines(BugReportJson.WriteLogLines(
        [
            Row(new string('x', 2500), $"System.Exception: {FiscalLink} failed", properties),
            Row(new string('x', 1990) + " " + FiscalLink),
        ], VerificationUrl))!;

        lines[0].Message.Should().Be(new string('x', 1999) + "…");
        lines[0].Exception.Should().Be("System.Exception: failed");
        var stored = JsonNode.Parse(lines[0].PropertiesJson!)!;
        stored["Url"]!.GetValue<string>().Should().BeEmpty();
        stored["Note"]!.GetValue<string>().Should().Be(new string('y', 1999) + "…");
        lines[1].Message.Should().Be(new string('x', 1990), "the link is stripped whole before the cut, never cut through");
    }

    // Spec P-22: existing events log Telegram ids - "{Stage} to bot message {BotMessageId}" - in
    // the rendered message and as properties. None reaches a report; a number that merely starts with one stays.
    [Fact]
    public void A_telegram_id_leaves_the_message_the_exception_and_the_properties()
    {
        var line = BugReportJson.ReadLogLines(BugReportJson.WriteLogLines(
        [
            Row("Replied to bot message 4567 in chat 111222333 for 45678 RSD",
                "System.InvalidOperationException: message 4567 not found",
                """{"Stage":"Replied","BotMessageId":4567,"ChatId":111222333,"VoiceFileId":"AwACAgIAAxkBAAIB","Amount":"45678"}"""),
        ], VerificationUrl))!.Single();

        line.Message.Should().Be("Replied to bot message [telegram id] in chat [telegram id] for 45678 RSD");
        line.Exception.Should().Be("System.InvalidOperationException: message [telegram id] not found");
        SameJson(line.PropertiesJson!, """
            {"Stage":"Replied","BotMessageId":"[telegram id]","ChatId":"[telegram id]","VoiceFileId":"[telegram id]","Amount":"45678"}
            """).Should().BeTrue(line.PropertiesJson);
        BugReportJson.TelegramIdMark.Should().Be("[telegram id]");
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("not json")]
    public void Properties_that_are_not_a_json_object_are_dropped(string properties)
    {
        BugReportJson.ReadLogLines(BugReportJson.WriteLogLines([Row("m", properties: properties)], VerificationUrl))!
            .Single().PropertiesJson.Should().BeNull();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("""[{"logged_at":"yesterday","level":"Warning","source":null,"message":"m","exception":null,"properties":null}]""")]
    [InlineData("""[{"logged_at":"2026-10-02T13:58:01.0000000+00:00","level":"Loud","source":null,"message":"m","exception":null,"properties":null}]""")]
    [InlineData("""[{"logged_at":"2026-10-02T13:58:01.0000000+00:00","level":"Warning","source":null,"exception":null,"properties":null}]""")]
    public void A_log_lines_column_that_cannot_be_read_back_reads_as_not_collected(string json)
    {
        BugReportJson.ReadLogLines(json).Should().BeNull();
    }
}
