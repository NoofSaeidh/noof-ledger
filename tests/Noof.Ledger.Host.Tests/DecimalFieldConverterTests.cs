using System.Globalization;
using AwesomeAssertions;
using MudBlazor.Extensions;
using Noof.Ledger.Web.Components;

namespace Noof.Ledger.Host.Tests;

// The operator types a decimal comma; MudBlazor's own converter reads "117,35" as 11735 under the invariant culture
// and turns what it cannot read into "no value" (amendment 27).
public class DecimalFieldConverterTests
{
    const string Refusal = "Not a number: use one decimal separator (a point or a comma) and no thousands separator.";

    [Theory]
    [InlineData("117,35", "117.35")]
    [InlineData("117.35", "117.35")]
    [InlineData("1,5", "1.5")]
    [InlineData("1.5", "1.5")]
    [InlineData("-1500,50", "-1500.50")]
    [InlineData(" 520 ", "520")]
    public void Reads_a_point_or_a_comma_as_the_decimal_separator(string typed, string expected)
    {
        var converter = new DecimalFieldConverter();

        converter.ConvertBack(typed).Should().Be(decimal.Parse(expected, CultureInfo.InvariantCulture));
        converter.Failed.Should().BeFalse();
    }

    [Theory]
    [InlineData("1.234,5")]
    [InlineData("1,234.5")]
    [InlineData("1,2,3")]
    [InlineData("1 234")]
    [InlineData("1e3")]
    [InlineData("abc")]
    public void Refuses_what_is_not_one_plain_number_instead_of_reading_it_as_nothing(string typed)
    {
        var converter = new DecimalFieldConverter();

        var act = () => converter.ConvertBack(typed);

        act.Should().Throw<FormatException>().WithMessage(Refusal);
        converter.Failed.Should().BeTrue();
    }

    [Fact]
    public void Mud_sees_a_refusal_as_a_conversion_error_not_as_an_empty_value()
    {
        var result = new DecimalFieldConverter().TryConvertBack("1.234,5");

        result.Success.Should().BeFalse();
        result.ExceptionError.Should().BeOfType<FormatException>().Which.Message.Should().Be(Refusal);
    }

    [Fact]
    public void An_empty_field_is_no_value_and_no_error()
    {
        var converter = new DecimalFieldConverter();

        converter.ConvertBack("").Should().BeNull();
        converter.Failed.Should().BeFalse();
    }

    [Fact]
    public void A_readable_value_after_an_unreadable_one_clears_the_failure()
    {
        var converter = new DecimalFieldConverter();
        var unreadable = () => converter.ConvertBack("1.234,5");
        unreadable.Should().Throw<FormatException>();

        converter.ConvertBack("1,5").Should().Be(1.5m);

        converter.Failed.Should().BeFalse();
    }

    [Fact]
    public void Shows_a_value_in_the_fields_format_and_culture()
    {
        var converter = new DecimalFieldConverter
        {
            Culture = () => CultureInfo.InvariantCulture,
            Format = () => "0.############",
        };

        converter.Convert(520.000000000000m).Should().Be("520");
        converter.Convert(117.35m).Should().Be("117.35");
        converter.Convert(null).Should().BeNull();
    }
}
