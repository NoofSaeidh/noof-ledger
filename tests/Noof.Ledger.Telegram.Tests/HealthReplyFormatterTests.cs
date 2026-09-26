using AwesomeAssertions;
using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Telegram.Tests;

public class HealthReplyFormatterTests
{
    static HealthItem Item(string name, HealthLevel level, string summary) =>
        new(name, level, summary, DateTimeOffset.Parse("2026-09-25T10:00:00Z"), string.Empty);

    [Fact]
    public void All_ok_checks_produce_the_all_good_header()
    {
        var report = new SystemHealthReport(HealthLevel.Ok,
            [Item("Database", HealthLevel.Ok, "ready"), Item("Backup", HealthLevel.Ok, "last success 2 h ago")]);

        var text = HealthReplyFormatter.Format(report);

        text.Should().Be("Health: all good\n✅ Database — ready\n✅ Backup — last success 2 h ago");
    }

    [Fact]
    public void A_single_warning_uses_the_singular_header()
    {
        var report = new SystemHealthReport(HealthLevel.Warning,
            [Item("Database", HealthLevel.Ok, "ready"), Item("Backup", HealthLevel.Warning, "last success 31 h ago")]);

        var text = HealthReplyFormatter.Format(report);

        text.Should().Be("Health: 1 warning\n✅ Database — ready\n⚠️ Backup — last success 31 h ago");
    }

    [Fact]
    public void Two_warnings_use_the_plural_header()
    {
        var report = new SystemHealthReport(HealthLevel.Warning,
            [Item("Backup", HealthLevel.Warning, "last success 31 h ago"), Item("Disk", HealthLevel.Warning, "3.2 GB free")]);

        var text = HealthReplyFormatter.Format(report);

        text.Should().StartWith("Health: 2 warnings\n");
    }

    [Fact]
    public void A_failing_check_wins_the_header_over_warnings()
    {
        var report = new SystemHealthReport(HealthLevel.Failing,
        [
            Item("Database", HealthLevel.Failing, "migration failed"),
            Item("Backup", HealthLevel.Warning, "last success 31 h ago"),
        ]);

        var text = HealthReplyFormatter.Format(report);

        text.Should().StartWith("Health: 1 failing\n");
        text.Should().Contain("❌ Database — migration failed");
        text.Should().Contain("⚠️ Backup — last success 31 h ago");
    }

    [Fact]
    public void An_empty_report_still_has_a_header()
    {
        var report = new SystemHealthReport(HealthLevel.Ok, []);

        var text = HealthReplyFormatter.Format(report);

        text.Should().Be("Health: all good");
    }
}
