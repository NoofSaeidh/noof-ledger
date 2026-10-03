using AwesomeAssertions;

namespace Noof.Ledger.Telegram.Tests;

public class BugCommandTests
{
    [Theory]
    [InlineData("/bug", null)]
    [InlineData("  /bug  ", null)]
    [InlineData("/bug the amount is wrong", "the amount is wrong")]
    [InlineData("/BUG@other_bot text", "text")]
    [InlineData("/bug@noof_ledger_bot", null)]
    [InlineData("/bug@noof_ledger_bot   spaced out  ", "spaced out")]
    [InlineData("/bug first line\nsecond line", "first line\nsecond line")]
    [InlineData("/bug\nonly on the next line", "only on the next line")]
    public void Recognises_the_command_with_or_without_a_bot_name_and_text(string text, string? expected)
    {
        BugCommand.TryParse(text, out var reportText).Should().BeTrue();
        reportText.Should().Be(expected);
    }

    [Theory]
    [InlineData("/bugfix 500")]
    [InlineData("/bugs")]
    [InlineData("/bug@")]
    [InlineData("coffee 250 /bug")]
    [InlineData("bug 500")]
    [InlineData("/health")]
    public void Text_that_only_looks_like_the_command_is_not_it(string text)
    {
        BugCommand.TryParse(text, out var reportText).Should().BeFalse();
        reportText.Should().BeNull();
    }

    [Fact]
    public void The_close_button_carries_its_label_and_the_reports_number()
    {
        var button = BugReportButtons.Close(12);

        button.Text.Should().Be("Close report");
        button.CallbackData.Should().Be("bug:close:12");
    }

    [Theory]
    [InlineData("bug:close:12", 12)]
    [InlineData("bug:close:1", 1)]
    public void Close_data_parses_back_to_its_report_number(string data, int expected)
    {
        BugReportButtons.TryParseClose(data, out var number).Should().BeTrue();
        number.Should().Be(expected);
    }

    [Theory]
    [InlineData("bug:close:abc")]
    [InlineData("bug:close:")]
    [InlineData("bug:close:0")]
    [InlineData("bug:close:-3")]
    [InlineData("bug:close:+12")]
    [InlineData("bug:close: 12")]
    [InlineData("bug:close:99999999999")]
    [InlineData("bug:close:１２")]
    [InlineData("bug:open:12")]
    [InlineData("cancel")]
    [InlineData(null)]
    public void Anything_else_is_not_a_close_press(string? data)
    {
        BugReportButtons.TryParseClose(data, out var number).Should().BeFalse();
        number.Should().Be(0);
    }

    [Theory]
    [InlineData("bug:close:12", true)]
    [InlineData("bug:anything", true)]
    [InlineData("BUG:close:12", false)]
    [InlineData("cancel", false)]
    [InlineData("record_anyway", false)]
    [InlineData(null, false)]
    public void Bug_report_data_is_told_apart_from_record_buttons_by_its_prefix(string? data, bool expected) =>
        BugReportButtons.IsBugReportData(data).Should().Be(expected);
}
