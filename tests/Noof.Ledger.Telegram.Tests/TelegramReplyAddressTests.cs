using System.Globalization;
using AwesomeAssertions;

namespace Noof.Ledger.Telegram.Tests;

public class TelegramReplyAddressTests
{
    // The BugReportsReplyTo migration back-fills telegram_chat_id::text || ':' || telegram_message_id::text, so a report
    // filed before it is found again on redelivery only if the address is written exactly that way.
    [Theory]
    [InlineData(111L, 5, "111:5")]
    [InlineData(555_000_001L, 2_147_483_647, "555000001:2147483647")]
    [InlineData(-1_001_234_567_890L, 42, "-1001234567890:42")]
    public void An_address_is_written_as_the_migration_wrote_it_and_reads_back_as_itself(long chatId, int messageId, string text)
    {
        var address = new TelegramReplyAddress(chatId, messageId);

        address.ToString().Should().Be(text);
        TelegramReplyAddress.Parse(text).Should().Be(address);
    }

    [Fact]
    public void An_address_is_written_in_invariant_digits_whatever_the_culture()
    {
        using var culture = new CultureScope("ar-SA");

        new TelegramReplyAddress(-111L, 5).ToString().Should().Be("-111:5");
    }

    [Theory]
    [InlineData("")]
    [InlineData("111")]
    [InlineData("111:")]
    [InlineData(":5")]
    [InlineData("111:5:6")]
    [InlineData("111:-5")]
    [InlineData("+111:5")]
    [InlineData("0111:5")]
    [InlineData("111:05")]
    [InlineData(" 111:5")]
    [InlineData("111:5 ")]
    [InlineData("111 :5")]
    [InlineData("-0:5")]
    [InlineData("111:2147483648")]
    [InlineData("111:٥")]
    [InlineData("tab 7")]
    public void Anything_else_is_refused(string text)
    {
        var act = () => TelegramReplyAddress.Parse(text);

        act.Should().Throw<FormatException>();
    }

    // A chat or message id never reaches a log line, and an exception's message can.
    [Fact]
    public void A_refused_address_is_not_named_in_the_refusal()
    {
        var act = () => TelegramReplyAddress.Parse("987654:05");

        act.Should().Throw<FormatException>().Which.Message.Should().NotContain("987654");
    }

    [Fact]
    public void A_delivered_reply_is_named_by_its_message_id_in_invariant_digits()
    {
        using var culture = new CultureScope("ar-SA");

        TelegramReplyAddress.DeliveredAs(901).Should().Be("901");
    }

    sealed class CultureScope : IDisposable
    {
        readonly CultureInfo previous = CultureInfo.CurrentCulture;

        public CultureScope(string name) => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);

        public void Dispose() => CultureInfo.CurrentCulture = previous;
    }
}
